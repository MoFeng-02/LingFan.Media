namespace LingFan.Media.Abstractions;

/// <summary>
/// 可实时节流的音频输出标记接口。无头（无硬件）音频输出实现它，
/// 使 <see cref="IMediaPlayer"/> 在 <see cref="ProcessingMode.Fastest"/> 下关闭实时节流
/// （瞬时提交、尽快处理完）。
/// 真实硬件输出（WASAPI 等）由硬件节奏限速，无需实现本接口。
/// </summary>
/// <remarks>零外部引用：仅依赖契约层中立类型。</remarks>
public interface IRealtimePacedOutput
{
    /// <summary>是否按真实节奏节流提交：true = 实时（默认）；false = 最快不节流。</summary>
    bool PaceRealTime { set; }
}
