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
    /// <remarks>
    /// <para>原生初始化刻意放在此处（Singleton 首次被 FFmpeg 工厂解析时构造），而非 <c>AddFFmpeg()</c> 注册期。
    /// 这样注册阶段保持纯 DI、绝不触碰原生库；只有真正用到 FFmpeg 后端（如其他后端不支持某源而回退）时才需要
    /// ffmpeg 原生 DLL 在场。注册一个后端 ≠ 马上要它的 native 库——这是“开箱即用 + 不侵入”的硬约束。</para>
    /// </remarks>
    /// <param name="logger">日志器。</param>
    /// <param name="options">FFmpeg 配置（含库路径与日志级别）。</param>
    public FFmpegBackend(ILogger<FFmpegBackend> logger, FFmpegOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            // 自绑定加载器：按平台与版本自适应加载原生库并建立 P/Invoke 解析器。
            // 即使未显式指定路径也须调用，确保首个 P/Invoke 前解析器已就绪（否则解析失败）。
            FF.Initialize(string.IsNullOrEmpty(options.FFmpegLibraryPath) ? null : options.FFmpegLibraryPath);

            // 设置 FFmpeg 日志级别（首个 FFmpeg P/Invoke 调用，触发原生绑定加载）。
            FF.av_log_set_level(options.LogLevel);

            // FFmpeg 全局网络初始化（幂等调用，多次调用安全）
            FF.avformat_network_init();
            _initialized = true;
            _logger.LogDebug("FFmpeg 全局初始化完成（日志级别={LogLevel}）", options.LogLevel);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "FFmpeg 全局初始化失败");
            throw;
        }
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
                FF.avformat_network_deinit();
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
