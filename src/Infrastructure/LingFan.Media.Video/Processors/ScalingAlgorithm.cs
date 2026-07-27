namespace LingFan.Media.Video.Processors;

/// <summary>
/// 缩放算法。
/// </summary>
/// <remarks>
/// 模块内枚举，仅 <see cref="ScalingProcessor"/> 使用，不放在 Abstractions
/// （处理器实现细节，非跨层契约）。
/// </remarks>
public enum ScalingAlgorithm : int
{
    /// <summary>最近邻采样：最快，画质较低（锯齿）。</summary>
    Nearest,

    /// <summary>双线性插值：速度与画质平衡，V2 默认算法（验收要求）。</summary>
    Bilinear,

    /// <summary>双三次插值：画质较高。V2 当前复用双线性以保证性能与稳定性，完整实现为后续增强。</summary>
    Bicubic,
}
