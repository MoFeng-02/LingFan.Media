namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 Metal 桩实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="Create"/> 返回 <see cref="MetalRenderer"/> 桩实例。</para>
/// <para>此工厂为同步配置（config 分类），无 I/O，无共享 GPU 设备（Metal 尚未实现）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class MetalRendererFactory : IVideoRendererFactory
{
    private readonly ILogger<MetalRenderer> _logger;

    /// <summary>
    /// 初始化 <see cref="MetalRendererFactory"/> 的新实例。
    /// </summary>
    public MetalRendererFactory(ILogger<MetalRenderer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        return new MetalRenderer();
    }
}
