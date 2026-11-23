namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频帧的色彩空间描述（由解码器从容器/输出格式透传，供渲染端选择正确的 YUV→RGB 转换）。
/// </summary>
/// <remarks>
/// <see cref="Standard"/> 与 <see cref="Range"/> 决定
/// YUV→RGB 的亮度/色差矩阵与分量偏移；<see cref="Transfer"/> 标识传输特性。
/// 前两者均未指定（<see cref="IsSpecified"/> 为 <see langword="false"/>）时，
/// 渲染端回退 BT.601-Full（保持既有默认行为）。
/// </remarks>
public readonly record struct VideoColorInfo(ColorStandard Standard, ColorRange Range, ColorTransfer Transfer)
{
    /// <summary>是否指定了至少一项色彩参数——渲染端据此判断是否需按指定矩阵转换。</summary>
    public bool IsSpecified => Standard != ColorStandard.Unspecified || Range != ColorRange.Unspecified;
}