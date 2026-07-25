namespace LingFan.Media.Avalonia;

/// <summary>
/// 视频呈现接口。抽象 VideoFrame → 可绘制图像的转换过程，使 VideoView 可切换不同呈现策略。
/// </summary>
/// <remarks>
/// <para>Avalonia 层接口，非 Abstractions。VideoView 委托给它，可切换 Skia / 原生 GPU。</para>
/// <para><b>异步策略</b>：全部同步（sync / native 分类）——
/// Initialize/Present/Clear/Resize/Dispose 均为 void，无 I/O。
/// Render 为 Avalonia Render(DrawingContext) 覆写，void 签名是框架硬限制。</para>
/// <para><b>线程模型</b>：</para>
/// <list type="bullet">
/// <item>Initialize/Resize：UI 线程</item>
/// <item>Present/Clear：渲染线程（管线调用方）</item>
/// <item>Render：Avalonia 渲染线程</item>
/// </list>
/// <para><b>AOT 兼容</b>：接口，实现应为 sealed 类。</para>
/// </remarks>
public interface IVideoPresenter : IDisposable
{
    /// <summary>绑定渲染目标。UI 线程调用。</summary>
    void Initialize(IRenderTarget target);

    /// <summary>
    /// 呈现一帧。渲染线程调用。
    /// 存储最新帧，旧帧 Dispose。如果渲染管线未消费帧，丢弃旧帧。
    /// </summary>
    void Present(VideoFrame frame);

    /// <summary>清除当前画面。渲染线程调用。</summary>
    void Clear();

    /// <summary>通知尺寸变化。UI 线程调用。</summary>
    void Resize(int width, int height, float scale);

    /// <summary>将缓存的图像绘制到 Avalonia DrawingContext。Avalonia 渲染线程调用。</summary>
    void Render(global::Avalonia.Media.DrawingContext drawingContext);
}
