using Avalonia.Media;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 由 Avalonia 合成树驱动的渲染器标记接口。实现者经 <see cref="Render"/> 将缓存图像绘制进
/// Avalonia 的 <see cref="DrawingContext"/>，并经 <see cref="Resize"/> 接收控件尺寸/DPI 变化。
/// </summary>
/// <remarks>
/// <para><see cref="VideoView"/> 在 <c>Render(DrawingContext)</c> 中借此区分两类渲染器：</para>
/// <list type="bullet">
/// <item><b>Avalonia 合成型</b>（如 <see cref="SkiaVideoRenderer"/>）：视频写入 WriteableBitmap，
/// 由本回调绘制进合成树——与 Avalonia 合成器共存，无黑屏/竞态。</item>
/// <item><b>原生 SwapChain 型</b>（如 D3D11/Vulkan/Metal/OpenGL）：经平台原生 SwapChain 合成上屏，
/// 不走本回调（其 Attach 需 Pointer/HWND，Avalonia 控件内必失败并回退到 Skia）。</item>
/// </list>
/// <para>接口置于 Avalonia UI 层（不污染 Abstractions 的 IVideoRenderer 中立契约）。</para>
/// </remarks>
public interface IAvaloniaRenderAware
{
    /// <summary>将缓存图像绘制到 Avalonia 合成树（Avalonia 渲染线程调用）。</summary>
    void Render(DrawingContext drawingContext);

    /// <summary>通知目标尺寸/DPI 变化（Avalonia 控件尺寸/DPI 变化）。</summary>
    void Resize(int width, int height, float scale);
}
