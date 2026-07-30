using LibVLCSharp.Shared;

namespace LingFan.Media.Backends.VLC;

/// <summary>
/// VLC 后端入口。持有 LibVLC 引擎实例（全局初始化状态）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 LibVLC 引擎实例，不持有任何媒体/播放上下文，
/// 多播放器共享安全（每个 VLCDemuxer 创建独立的 Media + MediaPlayer）。</para>
/// <para>构造函数和 Dispose 均为同步——LibVLC 初始化/释放是快速原生调用，无 I/O 阻塞。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class VLCBackend : IDisposable
{
    private readonly ILogger<VLCBackend> _logger;
    private readonly LibVLC _libVLC;
    private readonly VLCOptions _options;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VLCBackend"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="options">VLC 配置选项。</param>
    public VLCBackend(ILogger<VLCBackend> logger, VLCOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _options = options;


        // 构造 LibVLC 参数
        var args = new List<string>();
        // 无头兜底：禁止 VLC 打开原生视频窗口（不影响 SetVideoCallbacks 内存捕获路径）。
        if (options.Headless)
            args.Add("--vout=dummy");

        if (options.EnableHardwareDecoding)
        {
            args.Add("--avcodec-hw=any");
        }

        if (options.AdditionalOptions != null)
        {
            args.AddRange(options.AdditionalOptions);
        }

        _libVLC = args.Count > 0
            ? new LibVLC([.. args])
            : new LibVLC();

        _logger.LogDebug("VLC 后端初始化完成");
    }

    /// <summary>
    /// 获取 LibVLC 引擎实例（供 VLCDemuxer 创建 Media/MediaPlayer 使用）。
    /// </summary>
    internal LibVLC LibVLC => _libVLC;

    /// <summary>
    /// 获取 VLC 配置选项（供 VLCDemuxer 读取是否启用硬件解码等开关）。
    /// </summary>
    internal VLCOptions Options => _options;

    /// <summary>
    /// 释放 LibVLC 引擎资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _libVLC.Dispose();
            _logger.LogDebug("VLC 后端资源释放完成");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "VLC 后端资源释放异常");
        }
    }
}
