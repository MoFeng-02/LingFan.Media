using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Presenters.Vulkan;

/// <summary>
/// Vulkan GPU 视频呈现器工厂（创建 <see cref="VulkanGpuPresenter"/>）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：同步配置——纯 new，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class VulkanGpuPresenterFactory : IGpuPresenterFactory
{
    private readonly IVideoRendererFactory _rendererFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="VulkanGpuPresenterFactory"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">视频渲染器工厂（DI 注入）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public VulkanGpuPresenterFactory(IVideoRendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public Type PresenterType => typeof(VulkanGpuPresenter);

    /// <inheritdoc/>
    public IGpuPresenter Create() => new VulkanGpuPresenter(_rendererFactory, _loggerFactory);
}