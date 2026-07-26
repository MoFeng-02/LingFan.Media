using System.Buffers;
using LingFan.Media.Renderers.D3D11.SafeHandles;

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
/// <para><b>线程安全</b>：非线程安全。Attach/Detach 在 UI 线程，Present/Clear 在渲染线程，不可并发调用。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，pattern matching 匹配 IFrameResource 类型。</para>
/// <para><b>资源所有权</b>：ID3D11Device/ID3D11DeviceContext 由工厂持有（共享 Singleton，本类不 Dispose），
/// SwapChain/BackBuffer/RenderTargetView/StagingTexture 由本类持有（Session 级，Detach/Dispose 释放）。</para>
/// <para><b>V1 限制</b>：仅支持 BGRA32/RGBA32 像素格式，视频帧尺寸须与渲染目标尺寸一致（无缩放）。</para>
/// </remarks>
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

    private bool _disposed;
    private bool _attached;

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
            // 创建 SwapChain（通过 CreateSwapChainForHwnd，现代 API）
            _swapChain = factory.CreateSwapChainForHwnd(
                _device,
                hwnd,
                swapChainDesc,
                null,   // 无全屏描述（窗口模式）
                null);  // 无输出限制

            // 获取 BackBuffer
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);

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

    /// <inheritdoc/>
    public void Detach()
    {
        if (!_attached) return;

        ReleaseSessionResources();
        _attached = false;
        _logger.LogDebug("D3D11 渲染器已解绑渲染目标");
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_attached || _backBuffer is null || _swapChain is null)
        {
            throw new InvalidOperationException("渲染器未附加渲染目标，无法 Present。");
        }
        ArgumentNullException.ThrowIfNull(frame);

        // V1 限制：视频帧尺寸须与 BackBuffer 尺寸一致
        var backBufferDesc = _backBuffer.Description;
        if ((uint)frame.Width != backBufferDesc.Width ||
            (uint)frame.Height != backBufferDesc.Height)
        {
            throw new NotSupportedException(
                $"V1 限制：视频帧尺寸 {frame.Width}x{frame.Height} 须与渲染目标尺寸 " +
                $"{backBufferDesc.Width}x{backBufferDesc.Height} 一致。" +
                "请使用 SkiaVideoPresenter 进行缩放渲染。");
        }

        // Pattern matching 匹配 IFrameResource 类型（AOT 安全）
        // V2: Resource 可为 null（池化空壳），null 走 default 分支
        switch (frame.Resource)
        {
            case SoftwareFrameResource sw:
                PresentSoftwareFrame(sw, frame.Width, frame.Height);
                break;

            case D3D11TextureResource d3d11:
                PresentD3D11Texture(d3d11);
                break;

            default:
                throw new NotSupportedException(
                    $"不支持的帧资源类型：{frame.Resource?.GetType().Name ?? "null"}。" +
                    "D3D11 渲染器支持 SoftwareFrameResource 和 D3D11TextureResource。");
        }

        // 交换 SwapChain（VSync = 1）
        _swapChain.Present(1u, PresentFlags.None);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_attached || _renderTargetView is null || _swapChain is null) return;

        // 清除为透明黑色
        _context.ClearRenderTargetView(_renderTargetView, new Color4(0, 0, 0, 0));

        // 呈现清除后的画面（SyncInterval=0 立即显示，不等 VSync）
        _swapChain.Present(0u, PresentFlags.None);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 直接释放资源，不依赖 Detach()（Detach 检查 _attached，
        // 若 Attach 中途失败 _attached=false 会导致泄漏）
        ReleaseSessionResources();
        _attached = false;
        _logger.LogDebug("D3D11 渲染器已释放");
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

    // ── 内部方法 ──

    /// <summary>
    /// 软件帧渲染路径：CPU 数据 → D3D11 Texture → CopyResource → BackBuffer。
    /// </summary>
    private void PresentSoftwareFrame(SoftwareFrameResource sw, int width, int height)
    {
        // 确保暂存纹理存在且尺寸匹配
        EnsureStagingTexture((uint)width, (uint)height);

        uint rowPitch = (uint)(width * 4); // BGRA32/RGBA32 均为 4 字节/像素

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
    /// D3D11 纹理渲染路径：零拷贝 CopyResource。
    /// </summary>
    private void PresentD3D11Texture(D3D11TextureResource d3d11)
    {
        // 通过 SafeHandle 获取原始 COM 指针
        bool success = false;
        d3d11.Texture.DangerousAddRef(ref success);
        try
        {
            IntPtr ptr = d3d11.Texture.DangerousGetHandle();
            if (ptr == IntPtr.Zero)
            {
                throw new InvalidOperationException("D3D11 纹理句柄无效。");
            }

            // 创建临时 Vortice 包装器（不接管所有权——SafeHandle 持有引用）
            var srcTexture = new ID3D11Texture2D(ptr);
            try
            {
                _context.CopyResource(_backBuffer, srcTexture);
            }
            finally
            {
                // 抑制终结器——防止 Vortice 包装器的 finalizer 调用 Release
                // SafeHandle 负责调用 Marshal.Release
                GC.SuppressFinalize(srcTexture);
            }
        }
        finally
        {
            if (success)
            {
                d3d11.Texture.DangerousRelease();
            }
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
