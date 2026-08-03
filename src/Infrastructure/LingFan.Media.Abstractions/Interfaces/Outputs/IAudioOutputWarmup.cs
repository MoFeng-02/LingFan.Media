namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频输出预热能力（可选增强，探测模式同 <see cref="IBatchAudioSubmit"/>）。
/// </summary>
/// <remarks>
/// <para><b>解决的问题</b>：某些后端（典型如 WASAPI）首次 <c>IAudioClient.Initialize</c> 会触发操作系统音频引擎
/// （audiodg.exe）的一次性冷启动，产生 2~3s 的阻塞开销。若这笔开销发生在 <c>MediaPlayer.OpenAsync</c> 内部，
/// 用户会看到「窗口已出现却白/黑屏数秒」。实现本接口的后端可在 host 的<b>加载/启动界面期</b>提前调用
/// <see cref="WarmupAsync"/> 吸收这笔成本，使正式播放的 <c>OpenAsync</c> 几乎瞬时。</para>
/// <para><b>调用约定</b>：host 通过 <c>is IAudioOutputWarmup</c> 探测，不支持时（NoOp/其他平台后端）直接跳过；
/// 预热失败应被忽略（正式 <c>OpenAsync</c> 仍会完整初始化音频）。本接口不改变任何播放语义，仅是冷启动成本的<b>前移</b>。</para>
/// <para>库不代管「何时预热」——那是最终程序（加载界面/启动画面）的职责，符合「库暴露能力、host 决定时机」的边界。</para>
/// </remarks>
public interface IAudioOutputWarmup
{
    /// <summary>
    /// 预热音频子系统（如触发 OS 音频引擎首次拉起）。应在 host 加载界面期调用一次。
    /// 失败应被忽略，绝不能冒泡中断 host 启动。
    /// </summary>
    /// <param name="ct">取消令牌（host 卸载/超时用）。</param>
    Task WarmupAsync(CancellationToken ct = default);
}
