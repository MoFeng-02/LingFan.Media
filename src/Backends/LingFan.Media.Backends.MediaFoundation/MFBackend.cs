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
            int hr = MFInterop.MFStartup(MFConstants.MF_VERSION, MFConstants.MFSTARTUP_FULL);
            if (hr < 0)
            {
                throw new InvalidOperationException($"MFStartup 失败: HRESULT=0x{hr:X8}");
            }
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
                MFInterop.MFShutdown();
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
