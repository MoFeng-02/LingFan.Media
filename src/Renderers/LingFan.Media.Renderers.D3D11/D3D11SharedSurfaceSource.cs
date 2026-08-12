using LingFan.Media.Renderers.D3D11.Shaders;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// D3D11 共享表面源：把视频帧渲染进一块<b>可被宿主 Avalonia 合成器直接导入</b>的
/// 跨设备共享 D3D11 纹理（keyed mutex 保护），从而实现「无空域、纯控件级」的 GPU 上屏。
/// </summary>
/// <remarks>
/// <para><b>这是渲染器层唯一碰 D3D11 具体 API 的地方</b>。其余层（Avalonia <c>CompositionVideoRenderer</c>）
/// 只看到 <see cref="SharedGpuSurfaceDescriptor"/>（共享句柄 + 互斥键），<b>不引用任何 GPU 库</b>，
/// 从而达成「不绑定具体 GPU、低耦合」的架构诉求。</para>
/// <para><b>零拷贝路径</b>：解码侧 GPU 硬解（FFmpeg D3D11VA / MF DXVA）产出的 NV12/BGRA 纹理，
/// 经 <see cref="D3D11ShaderPipeline"/> 做 YUV→RGB + GPU 缩放后写入共享纹理 RTV；
/// 共享纹理由宿主 Avalonia 合成器直接采样上屏，<b>无 CPU 回读、无独占 HWND/空域</b>。</para>
/// <para><b>keyed mutex 握手（与 Avalonia GpuInterop 官方样例 D3DDemo 完全一致）</b>：</para>
/// <list type="bullet">
/// <item>生产者：<c>AcquireSync(0)</c> → 写 → <c>ReleaseSync(1)</c>；</item>
/// <item>消费者（Avalonia）：<c>UpdateWithKeyedMutexAsync(img, acquire=1, release=0)</c>
/// → <c>AcquireSync(1)</c> 采样 → <c>ReleaseSync(0)</c>。</item>
/// </list>
/// 故 <see cref="ConsumerAcquireKey"/>=<c>1</c>、<see cref="ConsumerReleaseKey"/>=<c>0</c>；
/// 生产者固定以 <c>0</c> 取锁、以 <c>1</c> 释放。互斥键恒 ≤ UInt32.MaxValue，适配
/// <c>UpdateWithKeyedMutexAsync</c> 的 <c>UInt32</c> 形参。</para>
/// <para><b>异步策略</b>：<see cref="TryWriteFrame"/> 同步（native 分类）——GPU 命令提交无真实 I/O await，
/// 补 async 即伪异步；且用<b>有限超时</b>裸 vtable <c>AcquireSync</c>，超时即丢帧，绝不阻塞管线线程。</para>
/// <para><b>线程</b>：由管线线程调用；共享 D3D11 设备已开启多线程保护（见 <see cref="D3D11MultithreadInterop"/>），
/// 且 keyed mutex 负责与 Avalonia 合成线程的跨设备同步。</para>
/// <para><b>AOT 兼容</b>：sealed 类，裸 vtable 互操作，无反射。</para>
/// </remarks>
internal sealed class D3D11SharedSurfaceSource : ISharedGpuSurfaceSource
{
    // ── keyed mutex 键（恒等关系，与官方样例握手一一对应）──
    // 生产者取锁键 = 消费者释放键 = 0；生产者释放键 = 消费者取锁键 = 1。
    private const ulong ProducerAcquireKey = 0;
    private const ulong ProducerReleaseKey = 1;
    // 生产者取锁超时（毫秒）。有限超时 → 消费方未及时归还时丢帧而非阻塞管线。
    private const int AcquireTimeoutMs = 16;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ILogger<D3D11SharedSurfaceSource> _logger;
    private readonly D3D11ShaderPipeline _pipeline;

    // 共享纹理 + RTV + keyed mutex（尺寸变化时重建；_version 随之递增）
    private ID3D11Texture2D? _sharedTexture;
    private ID3D11RenderTargetView? _renderTargetView;
    private IDXGIKeyedMutex? _keyedMutex;
    private IntPtr _kmPtr;
    private DxgiKeyedMutexInterop.AcquireSyncFn? _acquire;
    private DxgiKeyedMutexInterop.ReleaseSyncFn? _release;
    private nint _sharedHandle;

    private int _texW, _texH;
    private ulong _version;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="D3D11SharedSurfaceSource"/> 的新实例。
    /// </summary>
    /// <param name="device">共享 D3D11 设备（不由本类释放）。</param>
    /// <param name="context">共享 D3D11 设备上下文（不由本类释放）。</param>
    /// <param name="logger">日志。</param>
    internal D3D11SharedSurfaceSource(
        ID3D11Device device, ID3D11DeviceContext context, ILogger<D3D11SharedSurfaceSource> logger)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pipeline = new D3D11ShaderPipeline(_device, _context);
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind => SharedGpuHandleKind.D3D11TextureGlobalSharedHandle;

    /// <inheritdoc/>
    public ulong ConsumerAcquireKey => ProducerReleaseKey;

    /// <inheritdoc/>
    public ulong ConsumerReleaseKey => ProducerAcquireKey;

    /// <inheritdoc/>
    public SharedGpuSyncMode SyncMode => SharedGpuSyncMode.KeyedMutex;

    /// <inheritdoc/>
    public SharedGpuSemaphorePair? Semaphores => null;

    /// <inheritdoc/>
    public bool TryWriteFrame(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (_disposed)
            return false;
        if (frame.Resource is null)
            return false;

        // 路由：GPU 纹理帧（NV12/BGRA/RGBA）走零拷贝 shader 路径；
        // 软帧（SoftwareFrameResource）走 CPU 上传 shader 路径（仍 GPU 上屏，不回退 Skia）；
        // 其它类型交回调用方回退。
        ID3D11Texture2D? srcGpu = null;
        int w, h;
        PixelFormat fmt;
        int subresource = 0;

        if (frame.Resource is IGpuTextureResource gpu)
        {
            // 以同一 COM 指针构造 Vortice 包装（不 AddRef、不 Dispose，SafeHandle 持有引用，
            // 与 D3D11TextureResource.ReadbackToCpu 同源做法）。
            srcGpu = new ID3D11Texture2D(gpu.NativeTextureHandle);
            w = gpu.Width;
            h = gpu.Height;
            fmt = gpu.Format;
            subresource = gpu.SubresourceIndex;

            // 解码器无关 + 防跨设备非法绑定：零拷贝要求源纹理与共享表面位于同一 D3D11 设备。
            // 当前 FFmpeg D3D11VA 与 MF DXVA 均经 IGpuDeviceContext 绑定<b>同一</b>共享 D3D11 设备
            // （见 FFmpegVideoDecoder / MFVideoDecoder / MfDxgiDeviceManagerProvider），故直接采样即可；
            // 若某后端（如未来 VLC 或独立设备）产出自异设备的纹理，此处<b>优雅回退 Skia</b>
            // （ReadbackToCpu，两后端均已实现），绝不尝试跨设备绑定（会致白屏 / 设备移除）。
            // 此闸门使零拷贝路径对任意解码器保持中立：能同设备就零拷贝，不能就软兜底，绝不硬绑、绝不崩。
            using var srcDevice = srcGpu.Device;
            if (srcDevice.NativePointer != _device.NativePointer)
            {
                _logger.LogTrace("解码器纹理位于不同 D3D11 设备，跳过零拷贝（回退 Skia 软渲染）。");
                return false;
            }
        }
        else if (frame.Resource is SoftwareFrameResource sw)
        {
            w = sw.Width;
            h = sw.Height;
            fmt = sw.Format;
        }
        else
        {
            return false;
        }

        EnsureSharedTexture(w, h);
        if (_acquire is null || _release is null)
        {
            _logger.LogWarning("D3D11 共享纹理 keyed mutex 互操作不可用，跳过本帧。");
            return false;
        }

        // 生产者取锁（有限超时，超时=消费方未归还 → 丢帧）。
        int hr = _acquire(_kmPtr, ProducerAcquireKey, AcquireTimeoutMs);
        if (hr != 0)
        {
            _logger.LogTrace("D3D11 共享表面 AcquireSync 未成功 hr=0x{HR:X}（跳过本帧）", (uint)hr);
            return false;
        }

        bool written = false;
        try
        {
            if (srcGpu is not null)
            {
                // flipY=true：D3D11 RTV 原点在左上，Avalonia Composition 合成器按 OpenGL 风格
                //（原点在左下）采样共享纹理，写入时预翻转 Y 才能保证最终正向。
                if (fmt is PixelFormat.NV12 or PixelFormat.NV21)
                    _pipeline.PresentFromGpuTexture(srcGpu, subresource, w, h, _renderTargetView!, w, h, flipY: true);
                else if (fmt is PixelFormat.BGRA32 or PixelFormat.RGBA32)
                    _pipeline.PresentFromBgraGpuTexture(srcGpu, subresource, w, h, fmt, _renderTargetView!, w, h, flipY: true);
                else
                    return false; // 不支持的 GPU 格式 → 交回回退
            }
            else // SoftwareFrameResource
            {
                _pipeline.Present((SoftwareFrameResource)frame.Resource, _renderTargetView!, w, h, flipY: true);
            }

            written = true;
        }
        finally
        {
            // 取锁成功才释放；以 ConsumerAcquireKey(=1) 释放，交还消费方。
            if (written)
                _release(_kmPtr, ProducerReleaseKey);
            else
                _release(_kmPtr, ProducerAcquireKey); // 未写入也须归还锁，避免死锁
        }

        descriptor = new SharedGpuSurfaceDescriptor(
            _sharedHandle,
            SharedGpuHandleKind.D3D11TextureGlobalSharedHandle,
            w, h,
            SharedGpuSurfaceFormat.B8G8R8A8UNorm,
            _version,
            SharedGpuSyncMode.KeyedMutex);
        return true;
    }

    /// <summary>
    /// 确保共享纹理/RenderTargetView/keyed mutex 就绪，尺寸变化时重建底层纹理
    /// （_version 递增，消费方据此丢弃已缓存导入并重新导入）。
    /// </summary>
    private void EnsureSharedTexture(int w, int h)
    {
        if (_sharedTexture is not null && _texW == w && _texH == h)
            return;

        // 拆除旧资源（保留 _version 语义：重建才 +1）
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _keyedMutex?.Dispose();
        _keyedMutex = null;
        _kmPtr = IntPtr.Zero;
        _acquire = null;
        _release = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            // 关键：SharedKeyedMutex → 产生可被跨 D3D11 设备经 IDXGIResource.GetSharedHandle
            // 取出的 legacy 全局共享句柄，并启用 keyed mutex 跨设备同步。
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
        };
        _sharedTexture = _device.CreateTexture2D(desc);
        _renderTargetView = _device.CreateRenderTargetView(_sharedTexture, null);

        // 缓存 keyed mutex 裸指针 + 委托（QI 取引用，Dispose 时归还）
        _keyedMutex = _sharedTexture.QueryInterface<IDXGIKeyedMutex>();
        _kmPtr = _keyedMutex.NativePointer;
        _acquire = DxgiKeyedMutexInterop.GetAcquireDelegate(_kmPtr);
        _release = DxgiKeyedMutexInterop.GetReleaseDelegate(_kmPtr);

        // legacy 全局共享句柄（配对 IDXGIResource.GetSharedHandle；Avalonia ImportImage
        // 以 D3D11TextureGlobalSharedHandle 描述符打开）。句柄随纹理销毁自动失效，无需 CloseHandle。
        using var dxgiResource = _sharedTexture.QueryInterface<IDXGIResource>();
        _sharedHandle = dxgiResource.SharedHandle;

        _texW = w;
        _texH = h;
        _version++;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _keyedMutex?.Dispose(); // 归还 QI 引用（纹理随之可释放）
        _keyedMutex = null;
        _kmPtr = IntPtr.Zero;
        _acquire = null;
        _release = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _pipeline.Dispose();
    }
}
