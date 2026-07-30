namespace LingFan.Media.Abstractions;

/// <summary>
/// 无头 / 服务端处理模式。控制音视频同步与实时节流行为。
/// </summary>
public enum ProcessingMode
{
    /// <summary>
    /// 实时模式（默认）：主时钟以音频为基准按真实节奏推进，视频帧经同步器判
    /// Present / Wait / Drop。用于无头预览、或需跟外部时钟对齐的场景。
    /// </summary>
    RealTime = 0,

    /// <summary>
    /// 最快模式：关掉音视频同步（所有视频帧直接放行），无头音频输出不做实时节流
    /// （瞬时提交、尽快处理完）。用于转码、离线 ML 推理等批量处理、越快越好的场景。
    /// </summary>
    Fastest = 1,
}
