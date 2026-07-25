using LingFan.Media.Formats.Detection;

namespace LingFan.Media.Formats;

/// <summary>
/// <see cref="IMediaDemuxerFactory"/> 实现。探测流格式并路由到对应后端 Demuxer。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新的 Demuxer 实例（每次播放新建）。</para>
/// <para>使用 <see cref="FormatDetector"/> 探测容器格式，探测到的格式信息用于日志和未来优化。
/// V1 简化方案：始终委托给后端后备工厂创建实际 Demuxer 实例。</para>
/// <para>实际 Demuxer 实现（FFmpegDemuxer 等）由 Backends 模块通过 DI 注册。</para>
/// <para>如果后端通过 <c>AddFFmpeg()</c> 覆盖 <see cref="IMediaDemuxerFactory"/> 注册，
/// 则本类不会被使用。</para>
/// <para>DI 生命周期：Singleton 工厂。Session 内部对象由
/// <c>IMediaPlayerFactory.Create()</c> 手动 new 不走 DI 容器。</para>
/// </remarks>
public sealed class DemuxerFactory : IMediaDemuxerFactory
{
    private readonly ILogger<DemuxerFactory> _logger;
    private readonly Func<IMediaStream, IMediaDemuxer>? _fallbackFactory;

    /// <summary>
    /// 初始化 <see cref="DemuxerFactory"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="fallbackFactory">
    /// 后备 Demuxer 工厂（通常由 FFmpeg 后端通过 DI 注册）。
    /// 如果为 null，调用 <see cref="Create"/> 时将抛出异常。
    /// </param>
    public DemuxerFactory(
        ILogger<DemuxerFactory> logger,
        Func<IMediaStream, IMediaDemuxer>? fallbackFactory = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _fallbackFactory = fallbackFactory;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">stream 为 null。</exception>
    /// <exception cref="InvalidOperationException">未注册任何后端 Demuxer 工厂。</exception>
    public IMediaDemuxer Create(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // 探测容器格式（用于日志和未来优化）
        ContainerFormat format;
        try
        {
            format = FormatDetector.Detect(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 非致命异常降级为 Unknown，让后端自行探测
            // OutOfMemoryException 等致命异常不捕获，向上传播
            _logger.LogWarning(ex, "格式探测期间发生异常，将使用后端默认探测");
            format = ContainerFormat.Unknown;
        }

        if (format != ContainerFormat.Unknown)
        {
            _logger.LogDebug("探测到容器格式: {Format}", format);
        }
        else
        {
            _logger.LogDebug("未识别容器格式，使用后端自动探测");
        }

        // V1: 始终委托给后备工厂创建实际 Demuxer 实例
        if (_fallbackFactory is null)
        {
            throw new InvalidOperationException(
                "未注册任何后端 Demuxer 工厂。请先调用 AddFFmpeg() 或其他后端扩展方法注册 Demuxer 工厂。");
        }

        return _fallbackFactory(stream);
    }

    /// <inheritdoc/>
    public async Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // 探测容器格式（用于日志和未来优化）
        ContainerFormat format;
        try
        {
            format = await FormatDetector.DetectAsync(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 非致命异常降级为 Unknown，让后端自行探测
            // OutOfMemoryException 等致命异常不捕获，向上传播
            _logger.LogWarning(ex, "格式探测期间发生异常，将使用后端默认探测");
            format = ContainerFormat.Unknown;
        }

        if (format != ContainerFormat.Unknown)
        {
            _logger.LogDebug("探测到容器格式: {Format}", format);
        }
        else
        {
            _logger.LogDebug("未识别容器格式，使用后端自动探测");
        }

        // V1: 始终委托给后备工厂创建实际 Demuxer 实例
        if (_fallbackFactory is null)
        {
            throw new InvalidOperationException(
                "未注册任何后端 Demuxer 工厂。请先调用 AddFFmpeg() 或其他后端扩展方法注册 Demuxer 工厂。");
        }

        return _fallbackFactory(stream);
    }
}
