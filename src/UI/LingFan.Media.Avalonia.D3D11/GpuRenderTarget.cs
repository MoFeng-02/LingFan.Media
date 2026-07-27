using LingFan.Media.Abstractions;

namespace LingFan.Media.Avalonia.D3D11;

/// <summary>
/// 原生 GPU 渲染目标：包装窗口 HWND，供 <see cref="D3D11Renderer"/>.Attach 创建 SwapChain。
/// </summary>
/// <remarks>
/// <para>对应 D3D11Renderer.Attach 的要求：HandleType == Pointer 且 NativeHandle 为 IntPtr hwnd。</para>
/// <para>internal：仅桥接项目内使用，不暴露给 Avalonia UI 层（避免 UI 层感知 GPU  specifics）。</para>
/// </remarks>
internal sealed class GpuRenderTarget : IRenderTarget
{
    public GpuRenderTarget(IntPtr hwnd, int width, int height, float scale)
    {
        NativeHandle = hwnd;
        Width = width;
        Height = height;
        Scale = scale;
    }

    public RenderTargetType Type => RenderTargetType.Window;

    public RenderHandleType HandleType => RenderHandleType.Pointer;

    public object NativeHandle { get; }

    public int Width { get; }

    public int Height { get; }

    public float Scale { get; }
}
