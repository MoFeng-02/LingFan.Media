namespace LingFan.Media.Core;

/// <summary>
/// 媒体会话实现。持有打开媒体后的所有信息：轨道、元数据、轨道选择。
/// </summary>
/// <remarks>
/// <para>线程安全：Tracks 等属性在 Open 后只读，可跨线程读取。</para>
/// <para>轨道切换（SelectedVideoTrack 等 setter）通过 lock 保证原子性。</para>
/// <para>实际管线资源释放由 MediaPlayer.DisposeAsync 负责，CloseAsync 仅释放会话级信息。</para>
/// </remarks>
public sealed class MediaSession : IMediaSession
{
    private readonly object _lock = new();
    private MediaTrack? _selectedVideoTrack;
    private MediaTrack? _selectedAudioTrack;
    private MediaTrack? _selectedSubtitleTrack;
    private bool _closed;

    /// <summary>
    /// 初始化 <see cref="MediaSession"/> 的新实例。
    /// </summary>
    /// <param name="source">原始媒体源。</param>
    /// <param name="tracks">所有轨道列表。</param>
    /// <param name="metadata">媒体元数据。</param>
    /// <param name="duration">总时长。</param>
    /// <param name="isLive">是否直播流。</param>
    public MediaSession(
        IMediaSource source,
        IReadOnlyList<MediaTrack> tracks,
        MediaMetadata metadata,
        TimeSpan duration,
        bool isLive)
    {
        Source = source;
        Metadata = metadata;
        Duration = duration;
        IsLive = isLive;

        // 按 TrackType 分离轨道
        var videoTracks = new List<MediaTrack>();
        var audioTracks = new List<MediaTrack>();
        var subtitleTracks = new List<MediaTrack>();

        foreach (var track in tracks)
        {
            switch (track.Type)
            {
                case TrackType.Video:
                    videoTracks.Add(track);
                    break;
                case TrackType.Audio:
                    audioTracks.Add(track);
                    break;
                case TrackType.Subtitle:
                    subtitleTracks.Add(track);
                    break;
            }
        }

        VideoTracks = videoTracks;
        AudioTracks = audioTracks;
        SubtitleTracks = subtitleTracks;

        // 默认轨道选择：优先 IsDefault，否则选第一个
        _selectedVideoTrack = SelectDefault(VideoTracks);
        _selectedAudioTrack = SelectDefault(AudioTracks);
        _selectedSubtitleTrack = SelectDefault(SubtitleTracks);
    }

    /// <inheritdoc />
    public IMediaSource Source { get; }

    /// <inheritdoc />
    public MediaMetadata Metadata { get; }

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> VideoTracks { get; }

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> AudioTracks { get; }

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> SubtitleTracks { get; }

    /// <inheritdoc />
    public MediaTrack? SelectedVideoTrack
    {
        get { lock (_lock) return _selectedVideoTrack; }
        set { lock (_lock) _selectedVideoTrack = value; }
    }

    /// <inheritdoc />
    public MediaTrack? SelectedAudioTrack
    {
        get { lock (_lock) return _selectedAudioTrack; }
        set { lock (_lock) _selectedAudioTrack = value; }
    }

    /// <inheritdoc />
    public MediaTrack? SelectedSubtitleTrack
    {
        get { lock (_lock) return _selectedSubtitleTrack; }
        set { lock (_lock) _selectedSubtitleTrack = value; }
    }

    /// <inheritdoc />
    public TimeSpan Duration { get; }

    /// <inheritdoc />
    public bool IsLive { get; }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_closed) return Task.CompletedTask;
            _closed = true;
        }

        // 会话级信息清理。管线资源释放由 MediaPlayer.DisposeAsync 负责。
        return Task.CompletedTask;
    }

    private static MediaTrack? SelectDefault(IReadOnlyList<MediaTrack> tracks)
    {
        if (tracks.Count == 0)
            return null;

        // 优先 IsDefault 轨道
        foreach (var track in tracks)
        {
            if (track.IsDefault)
                return track;
        }

        // 否则选第一个
        return tracks[0];
    }
}
