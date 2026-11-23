namespace LingFan.Media.Extensions;

/// <summary>
/// 不支持后端的桩 DI 扩展方法（GStreamer / WebRTC）。
/// </summary>
/// <remarks>
/// <para>VLC / MediaFoundation 后端已有真实实现，
/// 分别迁移至各自的后端项目（LingFan.Media.Backends.VLCNative / .MediaFoundation），
/// 使用 <c>AddVLCNative()</c> / <c>AddMediaFoundation()</c> 扩展方法注册。</para>
/// <para>GStreamer 与 WebRTC <b>不支持</b>：Linux 场景由 VLC/FFmpeg 覆盖，WebRTC 不在本库范围内。
/// 两者统一为<b>注册期 fail-fast</b>：调用 <c>AddGStreamer()</c> / <c>AddWebRTC()</c>
/// 立即抛 <see cref="NotSupportedException"/>，不做「注册成功、运行时才抛」的延迟失败——
/// 消费方在装配阶段即得知不支持，而非播放中途。</para>
/// <para>所有方法为同步配置，无 I/O。</para>
/// </remarks>
public static class BackendStubs
{
    /// <summary>
    /// 注册 GStreamer 后端（不支持，注册期即抛）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">GStreamer 配置（未使用）。</param>
    /// <exception cref="NotSupportedException">GStreamer 后端不支持。</exception>
    public static MediaBuilder AddGStreamer(
        this MediaBuilder builder,
        Action<GStreamerOptions>? configure = null)
    {
        throw new NotSupportedException(
            "GStreamer 后端不支持。Linux 上请使用 FFmpeg 或 VLC 作为后端。" +
            "Windows 上可使用 FFmpeg / VLC / MediaFoundation。");
    }

    /// <summary>
    /// 注册 WebRTC 后端（不支持，注册期即抛）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">WebRTC 配置（未使用）。</param>
    /// <exception cref="NotSupportedException">WebRTC 后端不在当前范围。</exception>
    public static MediaBuilder AddWebRTC(
        this MediaBuilder builder,
        Action<WebRTCOptions>? configure = null)
    {
        throw new NotSupportedException(
            "WebRTC 后端不在当前范围。请使用 FFmpeg / VLC / MediaFoundation（Windows）" +
            "或 MediaCodec（Android）等已支持后端。");
    }
}

/// <summary>
/// GStreamer 后端配置选项（桩，保留供 API 兼容）。
/// </summary>
public sealed class GStreamerOptions
{
    /// <summary>插件搜索路径（可为 null，使用默认）。</summary>
    public string? PluginPath { get; set; }

    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecode { get; set; } = true;
}

/// <summary>
/// WebRTC 后端配置选项（桩，保留供 API 兼容）。
/// </summary>
public sealed class WebRTCOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecode { get; set; } = true;
}
