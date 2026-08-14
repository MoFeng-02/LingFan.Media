namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// Android 后端入口（Singleton）。持有全局配置与平台能力探测状态。
/// </summary>
/// <remarks>
/// <para>与 MFBackend 对称：MFBackend 持有 MFStartup 全局状态，本类持有 Android 后端全局状态
/// （当前主要是选项与平台能力标记）。</para>
/// <para><b>开箱即用</b>：构造函数不触碰任何 NDK 原生 API（不调用 AMediaCodec/AMediaExtractor），
/// 原生可用性探测延迟到 <see cref="IsSupportedAsync"/> 或实际 Open/Initialize 时执行——
/// 注册一个后端 ≠ 马上要它的 native 库（与 FFmpeg/VLC 后端同一延迟语义）。</para>
/// <para><b>仅 Android 可用</b>：实际平台检查在 demuxer.OpenAsync / decoder.Initialize 内执行
/// （<see cref="OperatingSystem.IsAndroid"/>），非 Android 直接抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
public sealed class AndroidBackend
{
    private readonly AndroidOptions _options;
    private readonly ILogger<AndroidBackend> _logger;

    /// <summary>后端选项（Singleton，纯 POCO，无原生依赖）。</summary>
    public AndroidOptions Options => _options;

    /// <summary>初始化 Android 后端入口的新实例。</summary>
    public AndroidBackend(AndroidOptions options, ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<AndroidBackend>();
    }

    /// <summary>
    /// 探测当前运行时是否可承载 Android 后端。
    /// </summary>
    /// <remarks>
    /// <para>接口契约：仅做平台判定（<see cref="OperatingSystem.IsAndroid"/>），无真实 I/O await 时返回
    /// <see cref="Task.FromResult{TResult}"/>（非伪异步）。</para>
    /// <para>NDK 原生库（libmediandk / libandroid）在 Android 运行时必然在场，无需额外探测；
    /// 非 Android 平台直接判否。</para>
    /// </remarks>
    public Task<bool> IsSupportedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool supported = OperatingSystem.IsAndroid();
        if (!supported)
        {
            _logger.LogDebug("[ANDROID] 当前运行时非 Android，Android 后端不可用");
        }
        return Task.FromResult(supported);
    }
}
