using LingFan.Media.Presenters;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 视频呈现器工厂（Avalonia UI 层）。继承中立的 <see cref="IGpuPresenterFactory"/>，
/// <see cref="Create"/> 协变返回 <see cref="IVideoPresenter"/>（比 IGpuPresenter 更具体）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯工厂方法，无 I/O。</para>
/// <para><b>AOT 兼容</b>：实现应为 sealed 类。</para>
/// </remarks>
public interface IVideoPresenterFactory : IGpuPresenterFactory
{
    /// <summary>创建视频呈现器实例（Avalonia 实现，含 Render 能力）。</summary>
    new IVideoPresenter Create();
}
