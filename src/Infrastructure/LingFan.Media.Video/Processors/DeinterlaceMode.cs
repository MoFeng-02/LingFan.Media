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
    /// <summary>Bob 去隔行：取顶场（偶数行）逐行复制填充完整帧；一进一出，帧率不变。简单快速，垂直细节减半。</summary>
    Bob,

    /// <summary>Blend 去隔行：混合相邻两场为一个帧，帧率不变。画质中等。</summary>
    Blend,

    /// <summary>Yadif 去隔行：预留的自适应去隔行模式；当前实现复用 Blend 处理（见 <see cref="Deinterlacer"/>）。</summary>
    Yadif,
}
