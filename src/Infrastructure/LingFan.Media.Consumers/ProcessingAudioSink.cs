using System;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头音频处理 Sink（无头 A 形态）：把音频帧（PCM）以只读借用方式交给下游计算，不播放到设备。
/// 订阅 <see cref="IMediaPlayer.AudioDataAvailable"/>，供频谱分析 / 音量检测 / 转码前处理 / ML 推理喂 PCM 等
/// 无头后端数据处理（无头核心竞争力）。
/// 帧为只读借用（管线在回调返回后释放），本类不在回调外持有帧引用、不 Dispose 外部帧。
/// </summary>
/// <remarks>
/// <para>对称于 <see cref="ProcessingFrameSink"/>（视频侧）：复用现有 <c>audioDataSink</c> 事件路由注入机制，
/// 管线侧代码零改动；无头场景下帧走 sink 分支，不进音频设备输出。</para>
/// <para>依赖倒置：仅依赖 Abstractions 中立类型，不引用任何渲染器 / 后端 / UI 模块。</para>
/// <para>AOT 兼容：<see langword="sealed"/> 类、无反射、纯事件订阅分发，遵守库整体 AOT 约束。</para>
/// <para>生命周期闭环：<see cref="Dispose"/> / <see cref="DisposeAsync"/> 取消订阅并清空附加状态，防事件泄漏；
/// 帧所有权始终归管线，本类永不 Dispose 外部帧。</para>
/// </remarks>
public sealed class ProcessingAudioSink : IHeadlessAudioConsumer
{
    private readonly Action<AudioFrame>? _onAudio;
    private IMediaPlayer? _attached;
    private bool _disposed;

    /// <summary>
    /// 初始化无头音频处理 Sink。
    /// </summary>
    /// <param name="onAudio">音频帧回调（只读借用，须同步拷贝所需 PCM）；可为 null（仅做订阅占位）。</param>
    public ProcessingAudioSink(Action<AudioFrame>? onAudio = null)
    {
        _onAudio = onAudio;
    }

    /// <summary>
    /// 订阅指定播放器的 <see cref="IMediaPlayer.AudioDataAvailable"/> 事件。幂等：重复调用先取消旧订阅。
    /// </summary>
    /// <param name="player">媒体播放器（无头场景通常为经 <c>AddHeadlessAudioOutput()</c> 构建的实例）。</param>
    public void Attach(IMediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessingAudioSink));
        if (_attached is not null) Detach();
        _attached = player;
        player.AudioDataAvailable += OnAudioFrame;
    }

    /// <summary>
    /// 取消订阅（若已订阅）。
    /// </summary>
    public void Detach()
    {
        if (_attached is null) return;
        _attached.AudioDataAvailable -= OnAudioFrame;
        _attached = null;
    }

    /// <inheritdoc />
    /// <remarks>帧为只读借用——本方法不持有、不 Dispose 传入的 <see cref="AudioFrame"/>。</remarks>
    public void Consume(AudioFrame frame)
    {
        _onAudio?.Invoke(frame);
    }

    private void OnAudioFrame(AudioFrame frame)
    {
        // 只读借用：不在本方法外持有 frame 引用，不 Dispose（所有权归管线）。
        Consume(frame);
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
