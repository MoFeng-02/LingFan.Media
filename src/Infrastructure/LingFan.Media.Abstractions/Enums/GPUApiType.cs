namespace LingFan.Media.Abstractions;

/// <summary>
/// GPU API 类型。
/// </summary>
public enum GPUApiType : int
{
    /// <summary>Direct3D 11（Windows）。</summary>
    D3D11,
    /// <summary>Vulkan（Windows/Linux/Android）。</summary>
    Vulkan,
    /// <summary>Metal（macOS/iOS）。</summary>
    Metal,
    /// <summary>OpenGL（桌面兼容备用）。</summary>
    OpenGL
}
