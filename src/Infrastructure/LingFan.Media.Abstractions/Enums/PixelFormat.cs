namespace LingFan.Media.Abstractions;

/// <summary>
/// 像素格式。
/// </summary>
public enum PixelFormat : int
{
    /// <summary>YUV 4:2:0 平面格式。</summary>
    YUV420P,
    /// <summary>YUV 4:2:2 平面格式。</summary>
    YUV422P,
    /// <summary>YUV 4:4:4 平面格式。</summary>
    YUV444P,
    /// <summary>NV12 半平面格式（Y + UV 交错）。</summary>
    NV12,
    /// <summary>NV21 半平面格式（Y + VU 交错）。</summary>
    NV21,
    /// <summary>BGRA 32位格式（B8G8R8A8）。</summary>
    BGRA32,
    /// <summary>RGBA 32位格式（R8G8B8A8）。</summary>
    RGBA32,
    /// <summary>RGB 24位格式（R8G8B8）。</summary>
    RGB24
}
