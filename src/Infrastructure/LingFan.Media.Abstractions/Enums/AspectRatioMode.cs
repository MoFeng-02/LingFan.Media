namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频宽高比缩放模式。
/// </summary>
public enum AspectRatioMode : int
{
    /// <summary>拉伸填满目标区域（不保持宽高比）。</summary>
    Fill,
    /// <summary>保持宽高比，留黑边。</summary>
    Uniform,
    /// <summary>保持宽高比，裁剪溢出部分。</summary>
    UniformToFill
}
