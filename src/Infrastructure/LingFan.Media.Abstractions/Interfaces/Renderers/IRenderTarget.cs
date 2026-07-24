namespace LingFan.Media.Abstractions;

/// <summary>
/// 渲染目标接口。
/// </summary>
/// <remarks>
/// <para>不叫 IRenderSurface——渲染目标不一定是窗口 Surface。</para>
/// <para>游戏引擎可渲染到 Texture → Material → Mesh。</para>
/// <para>NativeHandle 用 object 而非 IntPtr——AOT 安全（非热路径，显式 cast）。</para>
/// </remarks>
public interface IRenderTarget
{
    /// <summary>渲染目标类型（Window / Texture / Offscreen / Custom）。</summary>
    RenderTargetType Type { get; }

    /// <summary>渲染句柄类型（None / Pointer / Texture / Surface / Context）。</summary>
    RenderHandleType HandleType { get; }

    /// <summary>原生句柄（运行时显式 cast，不用反射）。</summary>
    object NativeHandle { get; }

    /// <summary>宽度。</summary>
    int Width { get; }

    /// <summary>高度。</summary>
    int Height { get; }

    /// <summary>DPI 缩放比。</summary>
    float Scale { get; }
}
