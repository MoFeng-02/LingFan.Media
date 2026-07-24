namespace LingFan.Media.Abstractions;

/// <summary>
/// 播放会话接口。持有打开媒体后的所有信息：轨道、元数据、轨道选择。
/// </summary>
/// <remarks>
/// 线程安全：Tracks 等属性在 Open 后只读，可跨线程读取。
/// </remarks>
public interface IMediaSession
{
    /// <summary>原始媒体源。</summary>
    IMediaSource Source { get; }

    /// <summary>媒体元数据。</summary>
    MediaMetadata Metadata { get; }

    /// <summary>所有视频轨道。</summary>
    IReadOnlyList<MediaTrack> VideoTracks { get; }

    /// <summary>所有音频轨道。</summary>
    IReadOnlyList<MediaTrack> AudioTracks { get; }

    /// <summary>所有字幕轨道。</summary>
    IReadOnlyList<MediaTrack> SubtitleTracks { get; }

    /// <summary>当前选中视频轨道。</summary>
    MediaTrack? SelectedVideoTrack { get; set; }

    /// <summary>当前选中音频轨道。</summary>
    MediaTrack? SelectedAudioTrack { get; set; }

    /// <summary>当前选中字幕轨道。</summary>
    MediaTrack? SelectedSubtitleTrack { get; set; }

    /// <summary>总时长。</summary>
    TimeSpan Duration { get; }

    /// <summary>是否直播流。</summary>
    bool IsLive { get; }

    /// <summary>关闭会话，释放资源。实际管线资源释放由 MediaPlayer.DisposeAsync 负责。</summary>
    Task CloseAsync(CancellationToken ct = default);
}
