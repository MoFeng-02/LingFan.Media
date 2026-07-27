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

    /// <inheritdoc />
    public Type PresenterType => typeof(SkiaVideoPresenter);
}
