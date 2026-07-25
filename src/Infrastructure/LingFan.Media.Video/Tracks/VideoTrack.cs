namespace LingFan.Media.Video;

/// <summary>
/// 视频轨道管理。封装轨道激活/停用状态。
/// </summary>
/// <remarks>
/// <para><see cref="Info"/> 类型为基类型 <see cref="MediaTrack"/>，
/// 与 <see cref="IMediaPlayer.VideoTracks"/>（<c>IReadOnlyList&lt;MediaTrack&gt;</c>）一致；
/// 视频专属参数在 <see cref="MediaTrack.VideoInfo"/>（<see cref="VideoTrackInfo"/>）中，
/// 本类不重复定义，保持 Video 模块与 Abstractions 的松散耦合。</para>
/// <para>非线程安全（激活/停用由 MediaPlayer 在 UI 线程或同步上下文中调用）。</para>
/// </remarks>
public sealed class VideoTrack
{
    /// <summary>轨道信息。</summary>
    public MediaTrack Info { get; }

    /// <summary>是否激活（激活后开始解码）。</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// 初始化 <see cref="VideoTrack"/> 的新实例。
    /// </summary>
    /// <param name="info">轨道信息。</param>
    /// <exception cref="ArgumentNullException">info 为 null。</exception>
    public VideoTrack(MediaTrack info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
    }

    /// <summary>
    /// 激活轨道（开始解码）。
    /// </summary>
    public void Activate() => IsActive = true;

    /// <summary>
    /// 停用轨道。
    /// </summary>
    public void Deactivate() => IsActive = false;
}
