using LingFan.Media.Abstractions;
using LingFan.Media.Avalonia;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia.D3D11;

/// <summary>
/// D3D11 GPU 视频呈现器工厂（创建 <see cref="D3D11GpuPresenter"/>）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯 new，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class D3D11PresenterFactory : IVideoPresenterFactory
{
    private readonly IVideoRendererFactory _rendererFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="D3D11PresenterFactory"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">视频渲染器工厂（DI 注入）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public D3D11PresenterFactory(IVideoRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public Type PresenterType => typeof(D3D11GpuPresenter);

    /// <inheritdoc/>
    public IVideoPresenter Create() => new D3D11GpuPresenter(_rendererFactory, _loggerFactory);
}
