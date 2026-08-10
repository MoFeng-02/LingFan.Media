namespace LingFan.Media.Extensions;

/// <summary>
/// GStreamer 后端桩 DI 扩展方法。
/// </summary>
/// <remarks>
/// <para>VLC / MediaFoundation / WebRTC 后端已在  中实现，
/// 分别迁移至各自的后端项目（LingFan.Media.Backends.VLCNative / .MediaFoundation / .WebRTC），
/// 使用 <c>AddVLCNative()</c> / <c>AddMediaFoundation()</c> / <c>AddWebRTC()</c> 扩展方法注册。</para>
/// <para>GStreamer 后端<b>决定不支持</b>（）：GStreamer 过于复杂，
/// 且 Linux 上已有 VLC 和 FFmpeg 作为充分的后端选择，无需 GStreamer。
/// 调用 <c>AddGStreamer()</c> 抛出 <see cref="NotSupportedException"/>。</para>
/// <para>所有方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class BackendStubs
{
    /// <summary>
    /// 注册 GStreamer 后端（不支持）。
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
