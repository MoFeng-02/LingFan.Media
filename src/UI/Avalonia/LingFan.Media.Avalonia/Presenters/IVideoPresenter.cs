using LingFan.Media.Presenters;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 视频呈现接口（Avalonia UI 层）。继承中立的 <see cref="IGpuPresenter"/>，补充
/// <see cref="Render"/>（Avalonia DrawingContext 绘制）——GPU Presenter 实现 IGpuPresenter 不含 Render（无空域渲染）。
/// </summary>
/// <remarks>
/// <para>Initialize / Present / Clear / Resize / Dispose 由 <see cref="IGpuPresenter"/> 提供；本接口仅增加 Render。</para>
/// <para><b>异步策略</b>：全部同步；Render 为 Avalonia Render(DrawingContext) 覆写，void 签名是框架硬限制。</para>
/// <para><b>AOT 兼容</b>：实现应为 sealed 类。</para>
/// </remarks>
public interface IVideoPresenter : IGpuPresenter
{
    /// <summary>将缓存的图像绘制到 Avalonia DrawingContext。Avalonia 渲染线程调用。</summary>
    void Render(global::Avalonia.Media.DrawingContext drawingContext);
}
