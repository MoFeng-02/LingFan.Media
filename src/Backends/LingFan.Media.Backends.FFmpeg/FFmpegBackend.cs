namespace LingFan.Media.Backends.FFmpeg;

/// <summary>
/// FFmpeg 后端入口。持有 FFmpeg 全局初始化状态。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 <c>avformat_network_init()</c> 等全局初始化状态，
/// 不持有任何媒体流/解码上下文，多播放器共享安全。</para>
/// <para>构造函数和 Dispose 均为同步——avformat_network_init/deinit 是快速原生调用，无 I/O 阻塞。</para>
/// <para>AOT 兼容：无反射，sealed 类。</para>
/// </remarks>
public sealed class FFmpegBackend : IDisposable
{
    private readonly ILogger<FFmpegBackend> _logger;
    private bool _disposed;
    private bool _initialized;

    /// <summary>
    /// 初始化 <see cref="FFmpegBackend"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    public FFmpegBackend(ILogger<FFmpegBackend> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            // FFmpeg 全局网络初始化（幂等调用，多次调用安全）
            ffmpeg.avformat_network_init();
            _initialized = true;
            _logger.LogDebug("FFmpeg 全局网络初始化完成");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "FFmpeg 全局网络初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 设置 FFmpeg 原生库搜索路径。
    /// </summary>
    /// <param name="path">原生库目录路径。</param>
    internal static void SetLibraryPath(string path)
    {
        // FFmpeg.AutoGen 通过 ffmpeg.RootPath 设置原生库搜索路径
        ffmpeg.RootPath = path;
    }

    /// <summary>
    /// 释放 FFmpeg 全局资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
        {
            try
            {
                ffmpeg.avformat_network_deinit();
                _logger.LogDebug("FFmpeg 全局网络清理完成");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "FFmpeg 全局网络清理异常");
            }
            _initialized = false;
        }
    }
}
