using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LingFan.Media.Abstractions;
using LingFan.Media.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LingFan.Media.Avalonia.D3D11;

/// <summary>
/// D3D11 原生 GPU 视频呈现器。实现 <see cref="IVideoPresenter"/>，内部委托 <see cref="IVideoRenderer"/>
/// （D3D11Renderer）将视频帧直接合成到窗口 SwapChain。
/// </summary>
/// <remarks>
/// <para><b>无空域渲染</b>：本 Presenter 的 <see cref="Render"/> 为 no-op——GPU 合成已由
/// <see cref="IVideoRenderer.Present"/> 完成（SwapChain present 到窗口 HWND）。Avalonia 控件层
/// （字幕、UI 叠加）位于窗口合成树之上，不受 GPU 合成影响。</para>
/// <para><b>HWND 解析</b>：<see cref="Initialize"/> 收到的 <see cref="IRenderTarget"/> 通常为
/// VideoView（HandleType.None，NativeHandle = this）。本 Presenter 从 NativeHandle 追溯
/// Visual → TopLevel → TryGetPlatformHandle().Handle 取得窗口 HWND，构造指针渲染目标传给
/// <see cref="IVideoRenderer.Attach"/>。</para>
/// <para><b>依赖倒置</b>：仅依赖 Abstractions 的 IVideoRenderer/IVideoRendererFactory 与 Avalonia 的
/// IVideoPresenter，不反向引用具体 GPU 类型；D3D11Renderer 具体类由 DI 注入的工厂创建。</para>
/// <para><b>异步策略</b>：Initialize/Present/Clear/Resize 同步（native 分类，GPU 操作无 I/O 可 await）；
/// Dispose 同步快速释放（renderer.Dispose 为快速 COM 调用，非伪异步）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class D3D11GpuPresenter : IVideoPresenter
{
    private readonly IVideoRendererFactory _rendererFactory;
    private readonly ILogger<D3D11GpuPresenter> _logger;
    private IVideoRenderer? _renderer;
    private IntPtr _hwnd;
    private int _width;
    private int _height;
    private float _scale;
    private bool _disposed;
    // 保护 _renderer 的并发访问：Present（管线线程）与 Resize/Dispose（UI 线程）互斥，
    // 防止 Detach 释放 SwapChain 与 Present 使用 SwapChain 原生竞态（D3D11Renderer 非线程安全）。
    private readonly object _rendererLock = new();

    /// <summary>
    /// 初始化 <see cref="D3D11GpuPresenter"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">视频渲染器工厂（DI 注入 IVideoRendererFactory，通常由 AddD3D11Renderer 注册）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public D3D11GpuPresenter(IVideoRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<D3D11GpuPresenter>();
    }

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        _hwnd = ResolveHwnd(target);
        _width = target.Width;
        _height = target.Height;
        _scale = target.Scale;

        // Create 后若 Attach 失败必须 Dispose 新建的渲染器，否则 COM 资源泄漏
        // （D3D11Renderer.Attach 内部仅释放 Session 级资源，不 Dispose 自身）。
        _renderer = _rendererFactory.Create();
        try
        {
            _renderer.Attach(CreatePointerTarget());
        }
        catch
        {
            _renderer.Dispose();
            _renderer = null;
            throw;
        }
        _logger.LogDebug("D3D11 GPU Presenter 已附加（{Width}x{Height}）", _width, _height);
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_renderer is null)
        {
            frame.Dispose();
            return;
        }
        // 锁内双重检查：防止 Dispose（UI 线程）置 _disposed 后仍在进行的 Present 触达已释放的 SwapChain。
        lock (_rendererLock)
        {
            if (_disposed || _renderer is null)
            {
                frame.Dispose();
                return;
            }
            _renderer.Present(frame);
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_rendererLock)
        {
            _renderer?.Clear();
        }
    }

    /// <inheritdoc/>
    public void Resize(int width, int height, float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer is null) return;
        _width = width;
        _height = height;
        _scale = scale;
        // 锁内重建 SwapChain：与 Present 互斥，防止 Detach 释放 SwapChain 与 Present 使用 SwapChain 原生竞态。
        lock (_rendererLock)
        {
            if (_disposed || _renderer is null) return;
            // 重建 SwapChain（Detach 释放 Session 级资源，Attach 用新尺寸重建）
            _renderer.Detach();
            _renderer.Attach(CreatePointerTarget());
        }
    }

    /// <inheritdoc/>
    public void Render(DrawingContext drawingContext)
    {
        // 无空域渲染：GPU 合成已由 IVideoRenderer.Present 完成，此处 no-op。
        // 视频内容位于窗口 SwapChain 层，Avalonia 字幕/UI 叠加层在其之上。
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 与 Present/Resize 互斥，防止释放期间仍有 Present 访问 SwapChain。
        lock (_rendererLock)
        {
            if (_renderer is not null)
            {
                _renderer.Detach();
                _renderer.Dispose();
                _renderer = null;
            }
        }
        _logger.LogDebug("D3D11 GPU Presenter 已释放");
    }

    private IRenderTarget CreatePointerTarget() => new GpuRenderTarget(_hwnd, _width, _height, _scale);

    private static IntPtr ResolveHwnd(IRenderTarget target)
    {
        // 已是指针类型：直接使用
        if (target.HandleType == RenderHandleType.Pointer && target.NativeHandle is IntPtr ptr)
            return ptr;

        // 否则从 NativeHandle 作为 Visual 追溯窗口 HWND
        if (target.NativeHandle is Visual visual)
        {
            var handle = TopLevel.GetTopLevel(visual)?.TryGetPlatformHandle()?.Handle;
            if (handle is not null && handle != IntPtr.Zero)
                return handle.Value;
        }

        throw new NotSupportedException(
            $"D3D11 GPU Presenter 无法从渲染目标解析窗口 HWND（HandleType={target.HandleType}）。" +
            "原生 GPU 模式需要窗口平台句柄。");
    }
}
