namespace LingFan.Media.Video.Processors;

/// <summary>
/// 去隔行扫描模式。
/// </summary>
/// <remarks>
/// 模块内枚举，仅 <see cref="Deinterlacer"/> 处理器使用，不放在 Abstractions
/// （处理器实现细节，非跨层契约）。
/// </remarks>
public enum DeinterlaceMode : int
{
    /// <summary>Bob 去隔行：将每个场扩展为完整帧，帧率翻倍。简单快速，画质较低。</summary>
    Bob,

    /// <summary>Blend 去隔行：混合相邻两场为一个帧，帧率不变。画质中等。</summary>
    Blend,

    /// <summary>Yadif 去隔行：自适应去隔行算法，画质较高，计算量较大。</summary>
    Yadif,
}
