using LingFan.Media.Abstractions;

namespace LingFan.Media.Presenters;

/// <summary>
/// 原生 GPU 渲染目标：包装窗口 HWND，供 <see cref="IVideoRenderer.Attach"/> 创建 SwapChain。
/// </summary>
/// <remarks>
/// <para>对应 D3D11Renderer.Attach 的要求：HandleType == Pointer 且 NativeHandle 为 IntPtr hwnd。</para>
/// <para><b>public</b>：中立层暴露，供 UI 层（如 Avalonia VideoView）在调用
/// <see cref="IGpuPresenter.Initialize"/> 前，把自身 Visual 解析出的 HWND 构造成本对象传入。
/// 这样 IGpuPresenter 实现不依赖任何 UI 框架即可获得窗口句柄。</para>
/// </remarks>
public sealed class GpuRenderTarget : IRenderTarget
{
    /// <summary>以窗口句柄构造渲染目标。</summary>
    /// <param name="hwnd">窗口原生句柄（HWND）。</param>
    /// <param name="width">窗口客户区宽度（像素）。</param>
    /// <param name="height">窗口客户区高度（像素）。</param>
    /// <param name="scale">显示缩放系数（DPI 缩放，1.0=100%）。</param>
    public GpuRenderTarget(IntPtr hwnd, int width, int height, float scale)
    {
        NativeHandle = hwnd;
        Width = width;
        Height = height;
        Scale = scale;
    }

    /// <inheritdoc/>
    public RenderTargetType Type => RenderTargetType.Window;

    /// <inheritdoc/>
    public RenderHandleType HandleType => RenderHandleType.Pointer;

    /// <inheritdoc/>
    public object NativeHandle { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public float Scale { get; }
}
