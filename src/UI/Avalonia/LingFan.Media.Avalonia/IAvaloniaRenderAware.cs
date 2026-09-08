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

    /// <summary>渲染器是否自带重绘调度（Present 后经 TopLevel 动画时钟预约下一帧重绘）。</summary>
    /// <remarks>
    /// <para>默认 false：<see cref="VideoView"/> 在每帧 Present 后投递一次
    /// <c>InvalidateVisual</c>，按内容帧率逐帧驱动整树重建——内容帧率高于调度能力时
    /// 重绘节拍落后于帧到达节拍，产生周期性跳帧（画面重复一拍）。</para>
    /// <para>实现方可返回 true 接管重绘调度：重绘改为跟随合成器动画时钟（即显示实际
    /// 刷新率，60/90/120Hz 自适应，无固定频率假设），仅在有待呈现新帧时预约，暂停零开销。</para>
    /// </remarks>
    bool DrivesOwnRenderLoop => false;
}
