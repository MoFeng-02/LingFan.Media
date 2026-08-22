namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频分量的取值区间（Full 或 Limited），决定 YUV→RGB 转换前是否需要偏移/缩放补偿。
/// </summary>
/// <remarks>
/// Limited（TV 区间）：Y∈[16,235]、色差∈[16,240]，转换前须做偏移与缩放补偿；
/// Full（PC 区间）：0..255 直接使用。各后端应将自身范围字段映射到本枚举；
/// 未指定（<see cref="ColorRange.Unspecified"/>）时渲染端回退默认。
/// </remarks>
public enum ColorRange : int
{
    /// <summary>未指定。</summary>
    Unspecified = 0,

    /// <summary>Full range（0..255）。</summary>
    Full = 1,

    /// <summary>Limited range（TV 区间）。</summary>
    Limited = 2
}