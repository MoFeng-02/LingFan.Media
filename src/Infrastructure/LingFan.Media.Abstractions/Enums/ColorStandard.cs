namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频的色彩原色与亮度矩阵标准（ITU-R BT 系列），用于 YUV→RGB 转换时选择正确的亮度/色差系数。
/// </summary>
/// <remarks>
/// 各后端（MediaCodec / FFmpeg 等）解码时应将自身的色彩标准字段映射到本枚举，
/// 渲染端据此选择相应转换矩阵。未指定（<see cref="ColorStandard.Unspecified"/>）时渲染端回退默认标准。
/// </remarks>
public enum ColorStandard : int
{
    /// <summary>未指定。渲染端回退默认标准。</summary>
    Unspecified = 0,

    /// <summary>ITU-R BT.601（SDTV）。<c>Kr=0.299, Kb=0.114</c>。</summary>
    Bt601 = 1,

    /// <summary>ITU-R BT.709（HDTV）。<c>Kr=0.2126, Kb=0.0722</c>。</summary>
    Bt709 = 2,

    /// <summary>ITU-R BT.2020（UHDTV）。</summary>
    Bt2020 = 3
}