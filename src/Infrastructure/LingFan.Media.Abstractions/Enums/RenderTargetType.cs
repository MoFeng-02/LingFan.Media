namespace LingFan.Media.Abstractions;

/// <summary>
/// 渲染目标类型。
/// </summary>
public enum RenderTargetType : int
{
    /// <summary>窗口表面（SwapChain 绑定到窗口）。</summary>
    Window,
    /// <summary>纹理目标（渲染到 GPU 纹理，游戏引擎可用）。</summary>
    Texture,
    /// <summary>离屏目标（无窗口渲染）。</summary>
    Offscreen,
    /// <summary>自定义目标。</summary>
    Custom
}
