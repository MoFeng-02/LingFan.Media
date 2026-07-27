using LingFan.Media.Abstractions;

namespace LingFan.Media.Presenters;

/// <summary>
/// 中立的视频 GPU 呈现接口。将 <see cref="IVideoRenderer"/>（D3D11 / Vulkan / OpenGL / Metal 等）的帧
/// 合成到窗口 SwapChain，与具体 UI 框架（Avalonia 等）解耦。
/// </summary>
/// <remarks>
/// <para><b>与 Avalonia 的 IVideoPresenter 区别</b>：本接口不含 <c>Render(DrawingContext)</c>——
/// GPU 合成由 <see cref="Present"/> 完成（无空域渲染），无需 UI 框架的 DrawingContext。
/// Avalonia 的 <see cref="LingFan.Media.Avalonia.IVideoPresenter"/> 继承本接口并补充 <c>Render</c>。</para>
/// <para><b>异步策略</b>：Initialize / Present / Clear / Resize 同步（native 分类，GPU 操作无 I/O 可 await）；
/// Dispose 同步快速释放（renderer.Dispose 为快速 COM 调用，非伪异步）。</para>
/// <para><b>依赖</b>：仅 Abstractions（IRenderTarget / VideoFrame）+ BCL。零 UI 框架、零具体 GPU 类型引用。</para>
/// <para><b>AOT 兼容</b>：实现应为 sealed 类，无反射。</para>
/// </remarks>
public interface IGpuPresenter : IDisposable
{
    /// <summary>绑定渲染目标（必须是 Pointer 类型，携带窗口 HWND）。UI 线程调用。</summary>
    void Initialize(IRenderTarget target);

    /// <summary>呈现一帧到窗口 SwapChain。渲染线程调用。</summary>
    void Present(VideoFrame frame);

    /// <summary>清除当前画面。渲染线程调用。</summary>
    void Clear();

    /// <summary>通知尺寸变化。UI 线程调用。</summary>
    void Resize(int width, int height, float scale);
}
