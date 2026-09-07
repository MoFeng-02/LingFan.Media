using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 视频合成的自定义视觉处理器（Avalonia 官方视频播放姿势）：
/// <see cref="CompositionCustomVisualHandler.OnAnimationFrameUpdate"/> 由渲染循环在渲染线程
/// <b>每帧回调</b>（不依赖 InvalidateVisual 的跨线程帧调度）——回调内检查待呈现帧并请求合成，
/// 绘制在 <see cref="OnRender"/>（lease 内经宿主呈现适配器采样共享表面）。
/// </summary>
/// <remarks>
/// <para><b>消息协议</b>：<see cref="StartMessage"/> 启动帧循环；<see cref="StopMessage"/> 停止；
/// 其他消息若为 <see cref="SkiaGpuVideoRenderer"/> 实例则绑定为帧来源。</para>
/// <para><b>线程</b>：<see cref="OnAnimationFrameUpdate"/> 与 <see cref="OnRender"/> 均在渲染线程
/// （Avalonia render loop），与 SkiaSharp lease 的上下文线程一致。</para>
/// </remarks>
internal sealed class VideoCompositionCustomVisualHandler : CompositionCustomVisualHandler
{
    /// <summary>启动帧循环消息。</summary>
    public static readonly object StartMessage = new();

    /// <summary>停止帧循环消息。</summary>
    public static readonly object StopMessage = new();

    private SkiaGpuVideoRenderer? _renderer;
    private bool _running;
    private long _frames;

    /// <summary>帧循环回调累计次数（0 = 渲染循环从未驱动本 visual）。</summary>
    public long FrameLoopCount => Interlocked.Read(ref _frames);

    /// <inheritdoc/>
    public override void OnMessage(object message)
    {
        // 诊断：消息通道可见化（Start 是否到达、时序如何——帧循环不启动的首要嫌疑在这里）。
        _renderer?.LogCompositionDiagnostic($"OnMessage: {(message as SkiaGpuVideoRenderer != null ? "BindRenderer" : message.GetType().Name)}");

        if (message == StartMessage)
        {
            _running = true;
            try
            {
                RegisterForNextAnimationFrameUpdate();
                _renderer?.LogCompositionDiagnostic("OnMessage(Start): RegisterForNextAnimationFrameUpdate 成功");
            }
            catch (Exception ex)
            {
                // Handler 未附加到 compositor（消息早于 server 端 attach）时 Register 会抛——
                // 吞掉并记录：帧循环不启动的直接证据。
                _renderer?.LogCompositionDiagnostic($"OnMessage(Start): Register 异常 {ex.GetType().Name}: {ex.Message}");
            }
        }
        else if (message == StopMessage)
        {
            _running = false;
        }
        else if (message is SkiaGpuVideoRenderer renderer)
        {
            _renderer = renderer;
        }
    }

    /// <inheritdoc/>
    /// <remarks>渲染循环每帧回调：有待呈现帧即请求合成（handler 自己的 Invalidate），并续订下一帧。</remarks>
    public override void OnAnimationFrameUpdate()
    {
        if (!_running)
            return;

        _frames++;
        if ((_frames % 60) == 1)
            _renderer?.LogCompositionDiagnostic($"OnAnimationFrameUpdate #{_frames}（帧循环已启动，visual={EffectiveSize.X}x{EffectiveSize.Y}）");

        if (_renderer is { HasPendingFrame: true })
            Invalidate(new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y));

        RegisterForNextAnimationFrameUpdate();
    }

    /// <inheritdoc/>
    /// <remarks>合成器请求渲染时执行：经宿主呈现适配器采样最新共享表面（GL 纹理 / Vulkan 纹理）。</remarks>
    public override void OnRender(ImmediateDrawingContext drawingContext)
    {
        if (_renderer is null)
            return;

        _renderer.RenderCompositionFrame(drawingContext, EffectiveSize);
    }
}
