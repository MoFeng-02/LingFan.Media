using System;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头帧处理 Sink（无头 A 形态）：把视频帧以零拷贝 / 零分配方式交给下游计算，不渲染、不显示。
/// 订阅 <see cref="IMediaPlayer.VideoFrameAvailable"/>，按资源类型分发：
/// <list type="bullet">
/// <item>GPU 硬解纹理（<see cref="IGpuTextureResource"/>）→ 原生句柄直给（零拷贝路径），不 Readback。</item>
/// <item>CPU 软解帧（<see cref="SoftwareFrameResource"/>）→ <see cref="Memory{T}.Span"/> 直读（零分配路径）。</item>
/// </list>
/// 帧为只读借用（管线在回调返回后释放），本类不在回调外持有帧引用、不 Dispose 外部帧。
/// </summary>
/// <remarks>
/// <para>无侵入：复用现有 <c>videoFrameSink</c> 路由注入机制，管线侧代码零改动；无头场景下帧走 sink 分支，不进渲染器 <c>Present</c>。</para>
/// <para>依赖倒置：仅依赖 Abstractions 中立类型，不引用任何渲染器 / 后端 / UI 模块。</para>
/// <para>AOT 兼容：<see langword="sealed"/> 类、无反射、纯接口 pattern matching 分发，遵守库整体 AOT 约束。</para>
/// <para>生命周期闭环：<see cref="Dispose"/> / <see cref="DisposeAsync"/> 取消订阅并清空附加状态，防事件泄漏；帧所有权始终归管线，本类永不 Dispose 外部帧。</para>
/// </remarks>
public sealed class ProcessingFrameSink : IHeadlessFrameConsumer
{
    private readonly Action<VideoFrame>? _onFrame;
    private readonly Action<IGpuTextureResource, VideoFrame>? _onGpu;
    private readonly Action<SoftwareFrameResource, VideoFrame>? _onCpu;
    private IMediaPlayer? _attached;
    private bool _disposed;

    /// <summary>
    /// 初始化无头帧处理 Sink。
    /// </summary>
    /// <param name="onFrame">统一帧回调（无论资源类型都会触发）；可为 null（仅用类型化回调）。</param>
    /// <param name="onGpu">GPU 纹理帧回调（零拷贝路径，句柄仅在回调内有效）；可为 null。</param>
    /// <param name="onCpu">CPU 帧回调（Span 直读路径）；可为 null。</param>
    public ProcessingFrameSink(
        Action<VideoFrame>? onFrame = null,
        Action<IGpuTextureResource, VideoFrame>? onGpu = null,
        Action<SoftwareFrameResource, VideoFrame>? onCpu = null)
    {
        _onFrame = onFrame;
        _onGpu = onGpu;
        _onCpu = onCpu;
    }

    /// <summary>
    /// 订阅指定播放器的 <see cref="IMediaPlayer.VideoFrameAvailable"/> 事件。幂等：重复调用先取消旧订阅。
    /// </summary>
    /// <param name="player">媒体播放器（无头场景通常为经 <c>AddHeadlessRenderer()</c> 构建的实例）。</param>
    public void Attach(IMediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessingFrameSink));
        if (_attached is not null) Detach();
        _attached = player;
        player.VideoFrameAvailable += Consume;
    }

    /// <summary>
    /// 取消订阅（若已订阅）。
    /// </summary>
    public void Detach()
    {
        if (_attached is null) return;
        _attached.VideoFrameAvailable -= Consume;
        _attached = null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 帧路由：GPU 零拷贝路径 vs CPU Span 路径。类型化回调优先，统一回调兜底。
    /// 帧为只读借用——本方法不持有、不 Dispose 传入的 <see cref="VideoFrame"/>。
    /// </remarks>
    public void Consume(VideoFrame frame)
    {
        if (_onGpu is not null && frame.Resource is IGpuTextureResource gpu)
        {
            _onGpu(gpu, frame);
        }
        else if (_onCpu is not null && frame.Resource is SoftwareFrameResource sfr)
        {
            _onCpu(sfr, frame);
        }

        _onFrame?.Invoke(frame);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
