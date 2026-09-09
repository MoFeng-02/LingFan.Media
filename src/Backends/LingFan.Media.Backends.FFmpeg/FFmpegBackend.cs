namespace LingFan.Media.Backends.FFmpeg;

/// <summary>
/// FFmpeg 后端入口。持有 FFmpeg 全局初始化状态。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 <c>avformat_network_init()</c> 等全局初始化状态，
/// 不持有任何媒体流/解码上下文，多播放器共享安全。</para>
/// <para>构造函数和 Dispose 均为同步——avformat_network_init/deinit 是快速原生调用，无 I/O 阻塞。</para>
/// <para>原生初始化唯一入口是 <see cref="EnsureNativeInitialized"/>：FFmpeg 各工厂在首次 Create 前调用它
/// （幂等），本类构造函数也调用它。这样 AddFFmpeg() 注册期与工厂 DI 解析期保持纯 DI、绝不触碰原生库；
/// 只有真正使用 FFmpeg 后端（工厂 Create）时才需要 ffmpeg 原生库在场。注册一个后端 ≠ 马上要它的
/// native 库——这是“开箱即用 + 不侵入”的硬约束（注册了 FFmpeg 但未部署原生库时，其他后端回退不受影响）。</para>
/// <para>AOT 兼容：无反射，sealed 类。</para>
/// </remarks>
public sealed class FFmpegBackend : IDisposable
{
    private readonly ILogger<FFmpegBackend> _logger;
    private bool _disposed;
    private bool _initialized;

    private static readonly object _ensureLock = new();
    private static bool _ensureDone;

    /// <summary>
    /// 初始化 <see cref="FFmpegBackend"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="options">FFmpeg 配置（含库路径与日志级别）。</param>
    public FFmpegBackend(ILogger<FFmpegBackend> logger, FFmpegOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            // 全局初始化收敛到 EnsureNativeInitialized（工厂 Create 前调用同一入口，幂等）。
            EnsureNativeInitialized(options);
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
    /// 确保 FFmpeg 原生绑定与全局状态就绪（幂等，线程安全；任何 FFmpeg P/Invoke 前调用）。
    /// </summary>
    /// <remarks>
    /// <para>自绑定加载器按平台与版本自适应加载原生库（如 avutil-60.dll / libavutil.so.60）并注册
    /// P/Invoke 解析器，随后应用日志级别并执行全局网络初始化。若不先初始化，首个 P/Invoke 会落到
    /// 默认库探测（只认无版本名），在带版本部署（libavformat.so.62 等）下必然解析失败。</para>
    /// </remarks>
    /// <param name="options">FFmpeg 配置（库路径与日志级别；null 时用默认探测路径与 AV_LOG_ERROR）。</param>
    internal static void EnsureNativeInitialized(FFmpegOptions? options)
    {
        lock (_ensureLock)
        {
            if (_ensureDone) return;
            FF.Initialize(string.IsNullOrEmpty(options?.FFmpegLibraryPath) ? null : options!.FFmpegLibraryPath);
            FF.av_log_set_level(options?.LogLevel ?? 16 /* AV_LOG_ERROR，与 FFmpegOptions 默认一致 */);
            FF.avformat_network_init();
            _ensureDone = true;
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
