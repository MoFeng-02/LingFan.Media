namespace LingFan.Media.Extensions;

/// <summary>
/// 后端桩 DI 扩展方法。VLC/MediaFoundation/GStreamer/WebRTC 后端尚未实现，
/// 调用时抛出 <see cref="NotSupportedException"/>。
/// </summary>
/// <remarks>
/// <para>未来实现时，创建独立的 LingFan.Media.Backends.VLC / MediaFoundation 等项目，
/// 将对应桩扩展方法迁移到各自的后端项目中。</para>
/// <para>所有方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class BackendStubs
{
    /// <summary>
    /// 注册 VLC 后端（尚未实现）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">VLC 配置（未使用）。</param>
    /// <exception cref="NotSupportedException">VLC 后端尚未实现。</exception>
    public static MediaBuilder AddVLC(
        this MediaBuilder builder,
        Action<VLCOptions>? configure = null)
    {
        throw new NotSupportedException(
            "VLC 后端尚未实现。未来将在 LingFan.Media.Backends.VLC 项目中实现。");
    }

    /// <summary>
    /// 注册 MediaFoundation 后端（尚未实现）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">MediaFoundation 配置（未使用）。</param>
    /// <exception cref="NotSupportedException">MediaFoundation 后端尚未实现。</exception>
    public static MediaBuilder AddMediaFoundation(
        this MediaBuilder builder,
        Action<MediaFoundationOptions>? configure = null)
    {
        throw new NotSupportedException(
            "MediaFoundation 后端尚未实现。未来将在 LingFan.Media.Backends.MediaFoundation 项目中实现。");
    }

    /// <summary>
    /// 注册 GStreamer 后端（尚未实现）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">GStreamer 配置（未使用）。</param>
    /// <exception cref="NotSupportedException">GStreamer 后端尚未实现。</exception>
    public static MediaBuilder AddGStreamer(
        this MediaBuilder builder,
        Action<object>? configure = null)
    {
        throw new NotSupportedException(
            "GStreamer 后端尚未实现。未来将在 LingFan.Media.Backends.GStreamer 项目中实现。");
    }

    /// <summary>
    /// 注册 WebRTC 后端（尚未实现）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">WebRTC 配置（未使用）。</param>
    /// <exception cref="NotSupportedException">WebRTC 后端尚未实现。</exception>
    public static MediaBuilder AddWebRTC(
        this MediaBuilder builder,
        Action<object>? configure = null)
    {
        throw new NotSupportedException(
            "WebRTC 后端尚未实现。未来将在 LingFan.Media.Backends.WebRTC 项目中实现。");
    }
}

/// <summary>
/// VLC 后端配置选项（桩）。
/// </summary>
/// <remarks>未来实现时迁移到 LingFan.Media.Backends.VLC 项目。</remarks>
public sealed class VLCOptions
{
}

/// <summary>
/// MediaFoundation 后端配置选项（桩）。
/// </summary>
/// <remarks>未来实现时迁移到 LingFan.Media.Backends.MediaFoundation 项目。</remarks>
public sealed class MediaFoundationOptions
{
}
