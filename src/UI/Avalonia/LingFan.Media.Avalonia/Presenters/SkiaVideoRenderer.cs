using Avalonia.Media;
using Avalonia.Media.Imaging;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Skia 软渲染视频渲染器（Avalonia UI 层）。实现中立的 <see cref="IVideoRenderer"/>，
/// 内部复用 <see cref="SkiaVideoPresenter"/> 的 WriteableBitmap 绘制逻辑，作为 <see cref="VideoView"/>
/// 回退链路的末级兜底。
/// </summary>
/// <remarks>
/// <para><b>无空域合成</b>：帧写入 Avalonia 的 WriteableBitmap（由 Avalonia 的 Skia 实例驱动），
/// VideoView.Render 经 <see cref="Render"/> 把它画进合成树——与 Avalonia 合成器共存，无黑屏/竞态。</para>
/// <para><b>GPU 友好</b>：解码侧仍走 GPU（FFmpeg D3D11VA / MF DXVA），本渲染器仅做最终 blit 到位图；
/// 不绑定固定 GPU，与「GPU 友好且不固定 GPU」诉求一致。</para>
/// <para><b>异步策略</b>：全部同步（Present/Clear/Resize/Render 纯内存+GPU 操作；Attach/Detach 同步）。
/// 绝对无伪异步——void 方法体内无 await。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；YUV→RGB 等逻辑在 SkiaVideoPresenter 内已实现。</para>
/// </remarks>
public sealed class SkiaVideoRenderer : IVideoRenderer, IAvaloniaRenderAware
{
    private readonly SkiaVideoPresenter _inner;

    /// <summary>构造 Skia 软渲染渲染器；可选注入日志器以增强失败可观测性。</summary>
    public SkiaVideoRenderer(ILogger? logger = null)
    {
        _inner = new SkiaVideoPresenter(logger);
    }

    /// <summary>宽高比模式。</summary>
    public AspectRatioMode AspectRatioMode
    {
        get => _inner.AspectRatioMode;
        set => _inner.AspectRatioMode = value;
    }

    /// <summary>测试可见：当前 WriteableBitmap（无帧时为 null）。</summary>
    internal WriteableBitmap? DebugBitmap => _inner.DebugBitmap;

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
        => _inner.Initialize(target);

    /// <inheritdoc/>
    public void Detach()
        => _inner.Dispose();

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>同步快速释放（sync 分类）：委托 <see cref="Dispose"/> + <see cref="ValueTask.CompletedTask"/>。
    /// Skia 软渲染释放为快速同步操作，无 I/O 可 await，非伪异步。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
        => _inner.Present(frame);

    /// <inheritdoc/>
    public void Clear()
        => _inner.Clear();

    /// <inheritdoc/>
    /// <remarks>Skia 软渲染经 Avalonia 合成上屏，渲染器不控制 vsync 呈现相位，端到端延迟为 0。</remarks>
    public TimeSpan PresentationLatency => TimeSpan.Zero;

    /// <summary>通知目标尺寸变化（Avalonia 控件尺寸/DPI 变化）。</summary>
    public void Resize(int width, int height, float scale)
        => _inner.Resize(width, height, scale);

    /// <summary>将缓存的图像绘制到 Avalonia DrawingContext（Avalonia 渲染线程调用）。</summary>
    /// <remarks>GPU 渲染器（D3D11 等）经原生 SwapChain 合成、不经本方法；本方法仅 Skia 软渲染路径使用。</remarks>
    public void Render(global::Avalonia.Media.DrawingContext drawingContext)
        => _inner.Render(drawingContext);

    /// <inheritdoc/>
    public void Dispose()
        => _inner.Dispose();
}
