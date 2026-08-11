using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// MediaFoundation 后端入口。持有 MF 平台初始化状态。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 <c>MFStartup</c> 全局初始化状态，
/// 不持有任何媒体流/解码上下文，多播放器共享安全。</para>
/// <para>构造函数和 Dispose 均为同步——<c>MFStartup</c>/<c>MFShutdown</c> 是快速 COM 调用，无 I/O 阻塞。</para>
/// <para><b>仅 Windows 可用</b>：非 Windows 平台构造时抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para>AOT 兼容：sealed 类，COM 互操作（MFStartup/MFShutdown 为纯 P/Invoke），无反射。</para>
/// </remarks>
public sealed class MFBackend : IDisposable
{
    private readonly ILogger<MFBackend> _logger;
    private bool _disposed;
    private bool _initialized;

    /// <summary>
    /// 初始化 <see cref="MFBackend"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台。</exception>
    public MFBackend(ILogger<MFBackend> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MediaFoundation 后端仅支持 Windows。请使用 FFmpeg 或 VLC 作为跨平台后端。");
        }

        try
        {
            // 经 MFPlatform 引用计数封装：真正的 MFShutdown 仅在所有消费者（解封装器 + 解码器）全部释放后才触发，
            // 避免一侧先释放把仍 in-flight 的原生 ReadSample 踩成 AV（MF 冷启动偶发崩溃的成因与规避点）。
            MFPlatform.Startup();
            _initialized = true;
            _logger.LogDebug("MediaFoundation 平台初始化完成");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not PlatformNotSupportedException)
        {
            _logger.LogError(ex, "MediaFoundation 平台初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 预热 MediaFoundation：提前打开一次样例媒体、强制激活解码器 MFT，把「进程首次
    /// <c>MFCreateSourceReaderFromURL</c> 激活 H.264/AAC 解码器」的 2~3s 冷启动成本挪到
    /// 调用方认为合适的位置（例如真实 App 的启动闪屏期、或探针创建可见窗口之前）。
    /// 正式 <see cref="MFDemuxer.OpenAsync"/> 复用已加载的解码器 DLL，打开几乎瞬时完成。
    /// </summary>
    /// <remarks>
    /// <para>幂等且容错：失败仅记 Debug 日志并静默返回，绝不抛异常影响正式打开。
    /// 内部经 <see cref="MFPlatform"/> 引用计数，与正式播放共享同一 MF 平台生命周期。</para>
    /// <para>仅 Windows 有效；非 Windows 直接 no-op。</para>
    /// </remarks>
    /// <param name="sampleUrl">用于触发解码器激活的任一媒体文件 URL/路径（通常与正式播放文件相同）。</param>
    public void Warmup(string? sampleUrl)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(sampleUrl))
            return;

        // 平台已在构造函数经 MFPlatform.Startup() 启动；此处再确保（幂等，引用计数 +1）。
        MFPlatform.Startup();

        int hr = MFInterop.MFCreateSourceReaderFromURL(sampleUrl!, IntPtr.Zero, out IntPtr reader);
        if (hr < 0 || reader == IntPtr.Zero)
        {
            _logger.LogDebug("MF 预热跳过：MFCreateSourceReaderFromURL 失败 HRESULT=0x{HR:X8}", hr);
            return;
        }

        try
        {
            // 强制激活解码器 MFT：首次 ReadSample 会触发 H.264/AAC 解码器实例初始化，
            // 进程首次冷启动的 2~3s 主要花在解码器 DLL 加载 + MFT 初始化上。提前付出，
            // 使正式 OpenAsync 复用已加载的解码器，把卡顿挪到窗口出现之前。
            // MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC（无视频流时该流读取失败属正常，下方 catch 吞掉）。
            var readSample = MfVTable.Get<IMFSourceReader_ReadSample>(reader, 6);
            readSample(reader, 0xFFFFFFFCu, 0, out _, out _, out long _, out IntPtr sample);
            if (sample != IntPtr.Zero)
                Marshal.Release(sample);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "MF 预热读样例外（忽略，不影响正式打开）");
        }
        finally
        {
            Marshal.Release(reader);
        }
    }

    /// <summary>
    /// 释放 MediaFoundation 平台资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
        {
            try
            {
                MFPlatform.Shutdown();
                _logger.LogDebug("MediaFoundation 平台资源释放完成");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "MediaFoundation 平台资源释放异常");
            }
            _initialized = false;
        }
    }
}
