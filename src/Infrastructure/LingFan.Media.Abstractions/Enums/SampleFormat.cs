namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频采样格式。
/// </summary>
public enum SampleFormat : int
{
    /// <summary>有符号 16 位整数。</summary>
    S16,
    /// <summary>有符号 32 位整数。</summary>
    S32,
    /// <summary>32 位浮点数。</summary>
    F32
}

// 注：FrameHandleType 枚举已移除，改为 IFrameResource 多态接口。
// sealed 实现：SoftwareFrameResource / D3D11TextureResource / VulkanImageResource /
// GLTextureResource / MetalTextureResource / CVPixelBufferResource / IOSurfaceResource。
// Renderer 用 pattern matching 匹配类型，AOT 安全。
