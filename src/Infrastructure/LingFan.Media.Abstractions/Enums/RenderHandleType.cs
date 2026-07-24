namespace LingFan.Media.Abstractions;

/// <summary>
/// 渲染句柄类型。
/// </summary>
public enum RenderHandleType : int
{
    /// <summary>无句柄（如 Skia UI 模式不需要原生句柄）。</summary>
    None,
    /// <summary>原始指针句柄（如 HWND / NSView 指针）。</summary>
    Pointer,
    /// <summary>GPU 纹理句柄（如 D3D11 Texture2D / VkImage）。</summary>
    Texture,
    /// <summary>表面句柄（如 EGLSurface / CAMetalLayer）。</summary>
    Surface,
    /// <summary>上下文句柄（如 GLContext / D3D11Device）。</summary>
    Context
}
