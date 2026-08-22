namespace LingFan.Media.Abstractions;

/// <summary>
/// 光电传输特性（OETF/EOTF 曲线），用于标识内容是否为 HDR。
/// </summary>
/// <remarks>
/// 主要为 SDR 内容提供标准 YUV→RGB；HDR 内容（PQ / HLG）暂不在本层做色调映射。
/// 各后端应将自身的传输特性字段映射到本枚举；未指定（<see cref="ColorTransfer.Unspecified"/>）时渲染端回退默认。
/// </remarks>
public enum ColorTransfer : int
{
    /// <summary>未指定。</summary>
    Unspecified = 0,

    /// <summary>线性。</summary>
    Linear = 1,

    /// <summary>SDR 视频（BT.709 / BT.601 标称）。</summary>
    SdrVideo = 3,

    /// <summary>ST2084（PQ，HDR10）。</summary>
    St2084 = 6,

    /// <summary>HLG（混合对数伽马）。</summary>
    Hlg = 7
}