namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// VLC Native 后端入口。持有自写 Apache-2.0 P/Invoke 驱动的 libvlc 引擎实例（全局初始化状态）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 libvlc 引擎实例，不持有任何媒体/播放上下文，
/// 多播放器共享安全（每个 VLCNativeDemuxer 创建独立的 Media + MediaPlayer）。</para>
/// <para>构造函数和 Dispose 均为同步——libvlc 初始化/释放是快速原生调用，无 I/O 阻塞。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
    /// <para>与原 LibVLCSharp 版 <c>VLCBackend</c> 的<b>行为完全对齐</b>：本后端同是<b>回调式 CPU 帧</b>模型
    /// （<c>libvlc_video_set_callbacks</c> 经 lock/unlock 拿 BGRA 内存）。<c>EnableHardwareDecoding</c> 时注入
    /// <c>--avcodec-hw=any</c>（「可用就硬解」）：<b>有头</b>（真实显示设备）走 D3D11VA 真硬解 + 回拷 CPU BGRA，
    /// 与老库 Headful 探针一致；<b>无头</b>（<c>--vout=dummy</c>，无显示设备）下 GPU 表面无法映射回 CPU → ffmpeg
    /// 报 <c>get_buffer() failed</c>，但 VLC 自动回退软解（帧仍以 CPU BGRA 交付，功能无损，仅日志有噪声）。
    /// VLC 后端在此架构里是「开箱即用回退中间件」，零拷贝硬解由 MF/ffmpeg 主路径承担。</para>
    /// <para>引擎替换方面与原版一致：仅把后端引擎从 <c>LibVLC</c>（LGPL）替换为自写 <c>LibVlcInstance</c>（Apache-2.0），
    /// 从而消除 NativeAOT 静态链接触发的 LGPL 义务。</para>
    /// </remarks>
public sealed class VLCNativeBackend : IDisposable
{
    private readonly ILogger<VLCNativeBackend> _logger;
    private readonly LibVlcInstance _instance;
    private readonly VLCOptions _options;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VLCNativeBackend"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="options">VLC 配置选项（来自共享层 <c>LingFan.Media.Backends.VLC.Abstractions</c>）。</param>
    public VLCNativeBackend(ILogger<VLCNativeBackend> logger, VLCOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        // 构造 libvlc 参数
        var args = new List<string>();
        // 无头兜底：禁止 VLC 打开原生视频窗口（不影响 SetVideoCallbacks 内存捕获路径）。
        if (options.Headless)
            args.Add("--vout=dummy");

        // 与旧 LibVLCSharp 版 VLCBackend 逐字对齐：启用硬件解码时注入 --avcodec-hw=any（「可用就硬解」）。
        // 无头(--vout=dummy)下 GPU 表面无法映射回 CPU，ffmpeg 会报 get_buffer() failed，但 VLC 自动回退软解，
        // 帧仍以 CPU BGRA 交付（功能无损，仅日志噪声；详见类文档与后端 README）。不按 Headless 强制 none，以免剥夺有头真硬解。
        if (options.EnableHardwareDecoding)
            args.Add("--avcodec-hw=any");

        if (options.AdditionalOptions != null)
            args.AddRange(options.AdditionalOptions);

        _instance = args.Count > 0
            ? new LibVlcInstance(args)
            : new LibVlcInstance();

        _logger.LogDebug("VLC Native 后端初始化完成 (libvlc {Version})", _instance.Version);
    }

    /// <summary>
    /// 获取 libvlc 引擎实例（供 VLCNativeDemuxer 创建 Media/MediaPlayer 使用）。
    /// </summary>
    internal LibVlcInstance Instance => _instance;

    /// <summary>
    /// libvlc 版本字符串（诊断用，如 <c>3.0.23.1 Vetinari</c>）。
    /// </summary>
    public string Version => _instance.Version;

    /// <summary>
    /// 获取 VLC 配置选项（供 VLCNativeDemuxer 读取是否启用硬件解码等开关）。
    /// </summary>
    internal VLCOptions Options => _options;

    /// <summary>
    /// 释放 libvlc 引擎资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _instance.Dispose();
            _logger.LogDebug("VLC Native 后端资源释放完成");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "VLC Native 后端资源释放异常");
        }
    }
}
