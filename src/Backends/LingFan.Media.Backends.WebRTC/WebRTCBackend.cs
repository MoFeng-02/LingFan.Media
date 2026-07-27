namespace LingFan.Media.Backends.WebRTC;

/// <summary>
/// WebRTC 后端入口。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。持有 WebRTC 全局初始化状态。</para>
/// <para><b>当前状态</b>：WebRTC 后端需要原生 WebRTC 库（如 Google libwebrtc C API 绑定），
/// 尚未集成。构造时不抛异常（允许 DI 注册），但 Demuxer/Decoder 运行时操作
/// 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para>构造函数和 Dispose 均为同步——无 I/O。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class WebRTCBackend : IDisposable
{
    private readonly ILogger<WebRTCBackend> _logger;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="WebRTCBackend"/> 的新实例。
    /// </summary>
    public WebRTCBackend(ILogger<WebRTCBackend> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogWarning("WebRTC 后端已注册，但原生 WebRTC 库尚未集成。运行时操作将抛 PlatformNotSupportedException。");
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
