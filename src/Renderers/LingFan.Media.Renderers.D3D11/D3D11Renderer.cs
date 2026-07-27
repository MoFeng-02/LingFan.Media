using System.Buffers;
using LingFan.Media.Renderers.D3D11.DirectComposition;
using LingFan.Media.Renderers.D3D11.SafeHandles;
using LingFan.Media.Renderers.D3D11.Shaders;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// Direct3D 11 视频渲染器。将 <see cref="VideoFrame"/> 呈现到 SwapChain。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>
/// （D3D11 设备由 <see cref="D3D11RendererFactory"/> 创建，无 I/O）。</item>
/// <item><see cref="Attach"/>/<see cref="Detach"/>：同步（native 分类），UI 线程，创建/释放 SwapChain。</item>
/// <item><see cref="Present"/>/<see cref="Clear"/>：同步（native 分类），渲染线程，GPU 操作。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类）。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。
/// D3D11 GPU 释放为快速同步 COM 调用，无 I/O 可 await，非伪异步。</item>
/// </list>
/// <para><b>线程安全（方案 A 修复）</b>：内部 <c>_gate</c> 锁串行化 Attach/Detach/Present/Clear/Dispose，
/// 可安全应对<b>管线线程 Present</b> 与 <b>UI 线程 Resize/Detach</b> 的并发竞态
/// （D3D11GpuPresenter 的 <c>_rendererLock</c> 与 Core VideoPipeline 的 Present 均汇聚到本锁）。
/// 单例渲染器被 UI 与 Core 管线共享时，本锁是唯一的跨调用方序列化点。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，pattern matching 匹配 IFrameResource 类型。</para>
/// <para><b>资源所有权</b>：ID3D11Device/ID3D11DeviceContext 由工厂持有（共享 Singleton，本类不 Dispose），
/// SwapChain/BackBuffer/RenderTargetView/StagingTexture 由本类持有（Session 级，Detach/Dispose 释放）。</para>
/// <para><b>V2-12 R4</b>：软件帧支持 GPU Shader 缩放（帧尺寸 ≠ 目标尺寸）与
/// YUV 像素格式（YUV420P/YUV422P/YUV444P/NV12/NV21，PS 内 BT.601 全范围转换）——
/// 经 <see cref="D3D11ShaderPipeline"/>。BGRA32/RGBA32 且尺寸一致仍走 V1 CopyResource 快路径。
/// GPU 纹理帧（D3D11TextureResource）维持 CopyResource 尺寸一致要求（硬解纹理无 SRV 绑定保证）。</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class D3D11Renderer : IVideoRenderer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ILogger<D3D11Renderer> _logger;

    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID3D11Texture2D? _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;

    /// <summary>R4 Shader 管线（懒创建；设备级资源，随渲染器 Dispose 释放，不随 Detach 释放）。</summary>
    private D3D11ShaderPipeline? _shaderPipeline;

    /// <summary>V2-15 R7：DirectComposition 互操作（无空域渲染，null=回退到 HWND SwapChain）。</summary>
    private D3D11CompositionInterop? _dcomp;

    private bool _disposed;
    private bool _attached;

    /// <summary>
    /// 串行化所有原生方法（Attach/Detach/Present/Clear/Dispose），化解管线线程 Present 与
    /// UI 线程 Resize/Detach 的并发原生竞态（方案 A）。普通 <see langword="lock"/>，同线程可重入。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 初始化 <see cref="D3D11Renderer"/> 的新实例。
    /// </summary>
    /// <param name="device">共享 D3D11 设备（不由本类释放）。</param>
    /// <param name="context">共享 D3D11 设备上下文（不由本类释放）。</param>
    /// <param name="logger">日志器。</param>
    internal D3D11Renderer(ID3D11Device device, ID3D11DeviceContext context, ILogger<D3D11Renderer> logger)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 是否已释放。供工厂判断缓存单例是否需要重建（方案 A 单例安全复用）。
    /// </summary>
    internal bool IsDisposed => _disposed;

    /// <inheritdoc/>
    /// <remarks>接口契约：设备由工厂创建，无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_disposed) return; // 锁外已检查，二次确认防御竞态

            if (_attached)
            {
                _logger.LogWarning("D3D11 渲染器已附加渲染目标，先 Detach 再 Attach。");
                Detach();
            }

            // 验证渲染目标句柄类型
            if (target.HandleType != RenderHandleType.Pointer)
            {
                throw new NotSupportedException(
                    $"D3D11 渲染器仅支持 {nameof(RenderHandleType.Pointer)} 句柄类型，收到 {target.HandleType}。");
            }

            if (target.NativeHandle is not IntPtr hwnd || hwnd == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "渲染目标的原生句柄无效——期望 IntPtr 类型的 HWND。", nameof(target));
            }

            if (target.Width <= 0 || target.Height <= 0)
            {
                throw new ArgumentException(
                    $"渲染目标尺寸无效：{target.Width}x{target.Height}。", nameof(target));
            }

            // 获取 DXGI 工厂（通过设备链获取，确保同一适配器）
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            // 创建 SwapChainDescription1（DXGI 1.2+，支持 FlipDiscard）
            var swapChainDesc = new SwapChainDescription1(
                (uint)target.Width,                        // Width
                (uint)target.Height,                       // Height
                Format.B8G8R8A8_UNorm,                     // Format
                false,                                      // Stereo
                Vortice.DXGI.Usage.RenderTargetOutput,     // BufferUsage（DXGI.Usage）
                2u,                                         // BufferCount
                Scaling.Stretch,                            // Scaling
                SwapEffect.FlipDiscard,                    // SwapEffect
                AlphaMode.Ignore,                          // AlphaMode
                SwapChainFlags.None);                       // Flags

            // 创建 SwapChain 及后续资源——若中途失败必须清理已创建的 COM 对象
            // （_attached 尚未设为 true，Detach() 不会清理，需 try-catch 兜底）
            try
            {
                // V2-15 R7：优先使用 DirectComposition（无空域渲染）
                // CreateSwapChainForComposition + DComp Visual 合成到窗口——视频帧不作为独立原生窗口
                bool dcompSuccess = false;
                try
                {
                    _swapChain = factory.CreateSwapChainForComposition(_device, swapChainDesc, null);
                    _dcomp = new D3D11CompositionInterop();
                    dcompSuccess = _dcomp.TryInitialize(hwnd, _swapChain.NativePointer);
                }
                catch
                {
                    dcompSuccess = false;
                }

                if (!dcompSuccess)
                {
                    // DirectComposition 不可用（旧版 Windows / 无桌面合成）——回退到 HWND SwapChain
                    _dcomp?.Dispose();
                    _dcomp = null;
                    _swapChain?.Dispose();
                    _swapChain = factory.CreateSwapChainForHwnd(
                        _device,
                        hwnd,
                        swapChainDesc,
                        null,   // 无全屏描述（窗口模式）
                        null);  // 无输出限制
                }

                // 获取 BackBuffer（_swapChain 在上方分支中已赋值——Composition 或 Hwnd 路径）
                _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);

                // 创建 RenderTargetView（用于 Clear）
                _renderTargetView = _device.CreateRenderTargetView(_backBuffer, null);
            }
            catch
            {
                // 清理已创建的部分资源，防止 COM 泄漏
                ReleaseSessionResources();
                throw;
            }

            _attached = true;
            _logger.LogDebug("D3D11 渲染器已附加渲染目标：{Width}x{Height}", target.Width, target.Height);
        }
    }

    /// <inheritdoc/>
    public void Detach()
    {
        lock (_gate)
        {
            if (_disposed || !_attached) return;

            ReleaseSessionResources();
            _attached = false;
            _logger.LogDebug("D3D11 渲染器已解绑渲染目标");
        }
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_disposed) return; // ���态兜底：Dispose 已在锁外置位，丢弃本帧避免触碰已释放 SwapChain
            if (!_attached || _backBuffer is null || _swapChain is null)
            {
                throw new InvalidOperationException("渲染器未附加渲染目标，无法 Present。");
            }

            var backBufferDesc = _backBuffer.Description;
            bool sizeMatches = (uint)frame.Width == backBufferDesc.Width &&
                               (uint)frame.Height == backBufferDesc.Height;

            // Pattern matching 匹配 IFrameResource 类型（AOT 安全）
            // V2: Resource 可为 null（池化空壳），null 走 default 分支
            switch (frame.Resource)
            {
                case SoftwareFrameResource sw:
                    // R4 分支决策：YUV 格式或尺寸不匹配 → Shader 路径（GPU 缩放 + YUV→RGB）；
                    // BGRA32/RGBA32 且尺寸一致 → V1 CopyResource 快路径（零 Shader 开销）
                    if (D3D11ShaderPipeline.IsYuvFormat(sw.Format) || !sizeMatches || sw.Format == PixelFormat.RGBA32)
                    {
                        _shaderPipeline ??= new D3D11ShaderPipeline(_device, _context);
                        _shaderPipeline.Present(sw, _renderTargetView!,
                            (int)backBufferDesc.Width, (int)backBufferDesc.Height);
                    }
                    else
                    {
                        PresentSoftwareFrame(sw, frame.Width, frame.Height);
                    }
                    break;

                case IGpuTextureResource gpu:
                    // V2-15 R5：GPU 纹理帧（DXVA 硬解输出）
                    // NV12/NV21：无 SRV 绑定，经中间 SRV 纹理 + Shader 缩放/转换
                    // BGRA32/RGBA32：尺寸一致时 CopySubresourceRegion（支持纹理数组）
                    if (D3D11ShaderPipeline.IsYuvFormat(gpu.Format))
                    {
                        _shaderPipeline ??= new D3D11ShaderPipeline(_device, _context);
                        PresentGpuTextureViaShader(gpu, (int)backBufferDesc.Width, (int)backBufferDesc.Height);
                    }
                    else if (sizeMatches)
                    {
                        PresentGpuTexture(gpu);
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"GPU 纹理帧尺寸 {frame.Width}x{frame.Height} 须与渲染目标尺寸 " +
                            $"{backBufferDesc.Width}x{backBufferDesc.Height} 一致（BGRA/RGBA CopyResource 路径），" +
                            $"或使用 NV12 格式经 Shader 缩放。");
                    }
                    break;

                default:
                    throw new NotSupportedException(
                        $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。" +
                        "D3D11 渲染器支持 SoftwareFrameResource 和 IGpuTextureResource。");
            }

            // 交换 SwapChain（VSync = 1）
            _swapChain.Present(1u, PresentFlags.None);
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_disposed || !_attached || _renderTargetView is null || _swapChain is null) return;

            // 清除为透明黑色
            _context.ClearRenderTargetView(_renderTargetView, new Color4(0, 0, 0, 0));

            // 呈现清除后的画面（SyncInterval=0 立即显示，不等 VSync）
            _swapChain.Present(0u, PresentFlags.None);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // 直接释放资源，不依赖 Detach()（Detach 检查 _attached，
            // 若 Attach 中途失败 _attached=false 会导致泄漏）
            ReleaseSessionResources();
            _shaderPipeline?.Dispose(); // R4：设备级 Shader 资源随渲染器释放
            _shaderPipeline = null;
            _attached = false;
            _logger.LogDebug("D3D11 渲染器已释放");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：D3D11 GPU 释放为快速同步 COM 调用，无 I/O 可 await。
    /// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 内部方法（均由加锁的公开方法调用，自身不再加锁）──

    /// <summary>
    /// 软件帧渲染路径：CPU 数据 → D3D11 Texture → CopyResource → BackBuffer。
    /// </summary>
    private void PresentSoftwareFrame(SoftwareFrameResource sw, int width, int height)
    {
        // 确保暂存纹理存在且尺寸匹配
        EnsureStagingTexture((uint)width, (uint)height);

        // 行距须尊重零拷贝原生帧的对齐 stride（V2-05 可能 Stride > width*4）；
        // 紧凑帧 Stride=0 退化为 width*4。与 D3D11ShaderPipeline.UploadPlanes 保持一致，避免行错位。
        uint rowPitch = (uint)(sw.Stride > 0 ? sw.Stride : width * 4);

        if (sw.Format == PixelFormat.BGRA32)
        {
            // BGRA32 → B8G8R8A8_UNorm：直接拷贝（布局一致）
            // UpdateSubresource<T>(ReadOnlySpan<T> srcData, ID3D11Resource dst, uint subresource, uint rowPitch, uint depthPitch, Box? dstBox)
            _context.UpdateSubresource<byte>(
                sw.Data.Span,
                _stagingTexture!,
                0u,
                rowPitch,
                0u,
                null);
        }
        else if (sw.Format == PixelFormat.RGBA32)
        {
            // RGBA32 → B8G8R8A8_UNorm：需要交换 R/B 通道
            int dataSize = width * height * 4;
            byte[] rented = ArrayPool<byte>.Shared.Rent(dataSize);
            try
            {
                Span<byte> bgra = rented.AsSpan(0, dataSize);
                var srcSpan = sw.Data.Span;
                int pixelCount = width * height;
                for (int i = 0; i < pixelCount; i++)
                {
                    bgra[i * 4 + 0] = srcSpan[i * 4 + 2]; // B ← R
                    bgra[i * 4 + 1] = srcSpan[i * 4 + 1]; // G ← G
                    bgra[i * 4 + 2] = srcSpan[i * 4 + 0]; // R ← B
                    bgra[i * 4 + 3] = srcSpan[i * 4 + 3]; // A ← A
                }

                _context.UpdateSubresource<byte>(
                    bgra,
                    _stagingTexture!,
                    0u,
                    rowPitch,
                    0u,
                    null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else
        {
            throw new NotSupportedException(
                $"D3D11 渲染器 V1 不支持像素格式 {sw.Format}，仅支持 BGRA32 和 RGBA32。");
        }

        // 拷贝暂存纹理到 BackBuffer
        _context.CopyResource(_backBuffer, _stagingTexture);
    }

    /// <summary>
    /// GPU 纹理渲染路径（BGRA32/RGBA32）：零拷贝 CopySubresourceRegion（支持纹理数组）。
    /// </summary>
    private void PresentGpuTexture(IGpuTextureResource gpu)
    {
        IntPtr ptr = gpu.NativeTextureHandle;
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("GPU 纹理句柄无效。");

        // 创建临时 Vortice 包装器（不接管所有权——帧资源持有引用）
        var srcTexture = new ID3D11Texture2D(ptr);
        try
        {
            if (gpu.SubresourceIndex > 0)
            {
                // 纹理数组：拷贝指定切片
                _context.CopySubresourceRegion(
                    _backBuffer, 0u, 0u, 0u, 0u,
                    srcTexture, (uint)gpu.SubresourceIndex, null);
            }
            else
            {
                // 单纹理：直接拷贝整个资源
                _context.CopyResource(_backBuffer, srcTexture);
            }
        }
        finally
        {
            // 抑制终结器——防止 Vortice 包装器的 finalizer 调用 Release
            GC.SuppressFinalize(srcTexture);
        }
    }

    /// <summary>
    /// GPU 纹理渲染路径（NV12/NV21）：经中间 SRV 纹理 + Shader 缩放/转换（V2-15 R5）。
    /// </summary>
    private void PresentGpuTextureViaShader(IGpuTextureResource gpu, int targetWidth, int targetHeight)
    {
        IntPtr ptr = gpu.NativeTextureHandle;
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("GPU 纹理句柄无效。");

        var srcTexture = new ID3D11Texture2D(ptr);
        try
        {
            _shaderPipeline!.PresentFromGpuTexture(
                srcTexture, gpu.SubresourceIndex,
                gpu.Width, gpu.Height,
                _renderTargetView!, targetWidth, targetHeight);
        }
        finally
        {
            GC.SuppressFinalize(srcTexture);
        }
    }

    /// <summary>
    /// 确保暂存纹理存在且尺寸匹配，尺寸变化时重建。
    /// </summary>
    private void EnsureStagingTexture(uint width, uint height)
    {
        if (_stagingTexture is not null &&
            _stagingWidth == width &&
            _stagingHeight == height)
        {
            return;
        }

        _stagingTexture?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,      // 仅作 CopyResource 源，无需绑定到管线
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device.CreateTexture2D(desc);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>
    /// 释放 Session 级资源（SwapChain / BackBuffer / RenderTargetView / StagingTexture）。
    /// 不释放共享设备/上下文（由工厂管理）。
    /// </summary>
    private void ReleaseSessionResources()
    {
        // V2-15 R7：先释放 DirectComposition（断开 SwapChain 与窗口的合成关联）
        _dcomp?.Dispose();
        _dcomp = null;

        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _stagingWidth = 0;
        _stagingHeight = 0;

        _renderTargetView?.Dispose();
        _renderTargetView = null;

        _backBuffer?.Dispose();
        _backBuffer = null;

        _swapChain?.Dispose();
        _swapChain = null;
    }
}
