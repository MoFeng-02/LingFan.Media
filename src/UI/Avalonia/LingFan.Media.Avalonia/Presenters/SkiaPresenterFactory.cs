using LingFan.Media.Presenters;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 默认 Skia 视频呈现器工厂（创建 <see cref="SkiaVideoPresenter"/>）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯 new，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class SkiaPresenterFactory : IVideoPresenterFactory
{
    /// <inheritdoc />
    public IVideoPresenter Create() => new SkiaVideoPresenter();

    // 显式实现基接口 Create（返回 IGpuPresenter），满足 IGpuPresenterFactory 契约；
    // IVideoPresenterFactory.Create()（new 声明，返回更具体的 IVideoPresenter）由上方 public Create() 实现。
    // C# 接口方法不支持返回类型协变，故基接口方法需显式实现。
    IGpuPresenter IGpuPresenterFactory.Create() => Create();

    /// <inheritdoc />
    public Type PresenterType => typeof(SkiaVideoPresenter);
}
