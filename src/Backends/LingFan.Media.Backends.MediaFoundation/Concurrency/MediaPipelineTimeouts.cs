using System;

namespace LingFan.Media.Backends.MediaFoundation.Concurrency;

/// <summary>
/// 关闭协议超时常量（固定值）。
/// </summary>
/// <remarks>
/// <para>D-3 决策：本轮采用固定内部常量，不暴露到 <c>MediaPlayerOptions</c>（可配置留待音视频管道重构统一处理，
/// 避免多一次契约面变更）。这些常量仅被 MF 后端内部使用（调度器 Join 与 gate 排空），属基础设施自愈参数。</para>
/// <para>取值理由：正常路径下在途原生调用在 &lt;100ms 内完成，5s 阈值是为覆盖「原生调用卡在设备 I/O」的极端情形；
/// 阈值越大越安全（泄漏概率越低），5s 已是合理上限。</para>
/// </remarks>
internal static class MediaPipelineTimeouts
{
    /// <summary>等待单线程调度器线程退出（CompleteAdding + Join）的上限。</summary>
    public static readonly TimeSpan SchedulerJoin = TimeSpan.FromSeconds(5);

    /// <summary>gate 等待在途原生调用排空（drain）的上限；超时即有意泄漏。</summary>
    public static readonly TimeSpan NativeDrain = TimeSpan.FromSeconds(5);
}
