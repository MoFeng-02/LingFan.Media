using LingFan.Media.Abstractions;

namespace ProbeShared;

/// <summary>
/// 把 HWND 包装成 <see cref="IRenderTarget"/>（Window 类型，Pointer 句柄）。
/// </summary>
public sealed class HwndRenderTarget : IRenderTarget
{
    private readonly IntPtr _hwnd;
    private readonly int _w, _h;

    /// <summary>以窗口句柄与实测客户区尺寸构造渲染目标。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="w">客户区宽（实测值）。</param>
    /// <param name="h">客户区高（实测值）。</param>
    public HwndRenderTarget(IntPtr hwnd, int w, int h) { _hwnd = hwnd; _w = w; _h = h; }

    /// <inheritdoc/>
    public RenderTargetType Type => RenderTargetType.Window;

    /// <inheritdoc/>
    public RenderHandleType HandleType => RenderHandleType.Pointer;

    /// <inheritdoc/>
    public object NativeHandle => _hwnd;

    /// <inheritdoc/>
    public int Width => _w;

    /// <inheritdoc/>
    public int Height => _h;

    /// <inheritdoc/>
    public float Scale => 1f;
}