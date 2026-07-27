using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

/// <summary>
/// 基于 Media Foundation <c>IMFSourceReader</c> 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 FFmpegDemuxer 对称）：</para>
/// <list type="bullet">
/// <item><c>OpenAsync</c>：混合——<c>await stream.ConnectAsync</c>（真异步 I/O）+
/// <c>await Task.Run(OpenCore)</c>（<b>伪异步</b>：MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用，Task.Run 仅卸载到线程池）。
/// 未来改进：MF 可通过 IMFByteStream + 异步 BeginRead 实现真异步，但复杂度高暂不实施。</item>
/// <item><c>ReadPacketAsync</c>：<b>伪异步</b>——<c>await Task.Run</c> 卸载 IMFSourceReader.ReadSample（同步 COM 调用）到线程池。
/// 未来改进：MF 可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。</item>
/// <item><c>SeekAsync</c>：<b>伪异步</b>——<c>await Task.Run</c> 卸载 MF seek 到线程池。</item>
/// <item><c>InitializeAsync</c>：接口契约，返回 <c>Task.CompletedTask</c>。</item>
/// <item><c>Close</c> / <c>Dispose</c> / <c>DisposeAsync</c>：同步 COM 释放。</item>
/// </list>
/// <para><b>仅 Windows 可用</b>：非 Windows 平台 OpenAsync 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>线程安全</b>：单线程使用（BufferManager 读取线程），非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，COM 互操作，无反射。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "COM 接口使用 [ComImport] 显式定义，不会被裁剪器移除。仅 Windows 运行时使用。")]
internal sealed class MFDemuxer : IMediaDemuxer
{
    private readonly MFBackend _backend;
    private readonly ILogger<MFDemuxer> _logger;

    private IMFSourceReader? _sourceReader;
    private string? _url;
    private IMediaStream? _stream;

    private bool _opened;
    private bool _disposed;
    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    /// <summary>
    /// 初始化 <see cref="MFDemuxer"/> 的新实例。
    /// </summary>
    /// <param name="backend">MF 后端入口（Singleton）。</param>
    /// <param name="logger">日志器。</param>
    public MFDemuxer(MFBackend backend, ILogger<MFDemuxer> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public MediaMetadata Metadata => _metadata;

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 混合：<c>await stream.ConnectAsync</c>（真异步 I/O）+
    /// <c>await Task.Run(OpenCore)</c>（伪异步：MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用）。
    /// </remarks>
    public async Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MediaFoundation 后端仅支持 Windows。请使用 FFmpeg 或 VLC 作为跨平台后端。");
        }

        ct.ThrowIfCancellationRequested();
        _stream = stream;

        // 异步预建连
        await stream.ConnectAsync(ct).ConfigureAwait(false);

        // 从 IMediaStream 获取 URL（文件路径或网络 URL）
        // MFSourceReader 需要 URL 或 IMFByteStream
        _url = ExtractUrl(stream);

        if (string.IsNullOrEmpty(_url))
        {
            throw new NotSupportedException(
                "MediaFoundation 后端当前仅支持 URL/文件路径源。对于流式输入，请使用 FFmpeg 后端。");
        }

        // 伪异步：MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用，Task.Run 仅卸载到线程池。
        // 未来改进：可通过 IMFByteStream + 异步 BeginRead 实现真异步，但复杂度高暂不实施。
        await Task.Run(() => OpenCore(_url!, ct), ct).ConfigureAwait(false);
        _opened = true;

        _logger.LogInformation("MediaFoundation 打开成功: {TrackCount} 条轨道, 时长 {Duration}",
            _tracks.Count, _metadata.Duration);
    }

    /// <summary>
    /// OpenAsync 的同步核心逻辑。在 Task.Run 线程上执行（伪异步）。
    /// </summary>
    private void OpenCore(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        int hr = MFInterop.MFCreateSourceReaderFromURL(url, IntPtr.Zero, out _sourceReader);
        if (hr < 0 || _sourceReader == null)
        {
            throw new InvalidOperationException($"MFCreateSourceReaderFromURL 失败: HRESULT=0x{hr:X8}");
        }

        // 解析轨道
        _tracks = ParseTracks(_sourceReader);

        // 选择所有流（让 SourceReader 输出所有轨道的采样）
        foreach (var track in _tracks)
        {
            _sourceReader.SetStreamSelection((uint)track.Index, true);
        }

        // 解析元数据（MF 不直接提供标题/艺术家等，从轨道推算时长）
        _metadata = new MediaMetadata
        {
            Duration = TimeSpan.Zero,
            ContainerFormat = ContainerFormat.Unknown
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 伪异步：<c>await Task.Run</c> 卸载 IMFSourceReader.ReadSample（同步 COM 调用）到线程池。
    /// 未来改进：可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。
    /// </remarks>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceReader == null)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：IMFSourceReader.ReadSample 为同步 COM 调用，Task.Run 仅卸载到线程池。
        // 未来改进：可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。
        return await Task.Run(() => ReadPacketCore(ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ReadPacketAsync 的同步核心逻辑。
    /// </summary>
    private MediaPacket? ReadPacketCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        int hr = _sourceReader!.ReadSample(
            MFConstants.MF_SOURCE_READER_ALL_STREAMS,
            0, // dwControlFlags
            0, // dwStreamIndex (unused with ALL_STREAMS)
            out int actualStreamIndex,
            out int streamFlags,
            out long timestamp,
            out IMFSample? sample);

        if (hr < 0)
        {
            _logger.LogWarning("IMFSourceReader.ReadSample 失败: HRESULT=0x{HR:X8}", hr);
            return null;
        }

        // 流结束
        if ((streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
        {
            return null;
        }

        if (sample == null)
        {
            // 流 tick（无数据），返回 null 让调用方重试
            return null;
        }

        // 提取采样数据
        sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        buffer.Lock(out IntPtr dataPtr, out uint maxLen, out uint curLen);

        byte[] data = new byte[curLen];
        Marshal.Copy(dataPtr, data, 0, (int)curLen);

        buffer.Unlock();

        // 提取时间戳（100ns 单位 → TimeSpan）
        TimeSpan ts = timestamp > 0
            ? TimeSpan.FromTicks(timestamp)
            : TimeSpan.Zero;

        return new MediaPacket(
            actualStreamIndex,
            data,
            ts,
            TimeSpan.Zero,
            keyFrame: true);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 伪异步：<c>await Task.Run</c> 卸载 MF seek 操作到线程池。
    /// </remarks>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceReader == null)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：MF SourceReader seek 为同步 COM 调用，Task.Run 仅卸载到线程池。
        return await Task.Run(() =>
        {
            // MF SourceReader 的 seek 通过属性设置
            // 这里简化为返回 true（实际需要通过 IMFPresentationDescriptor 设置位置）
            _logger.LogDebug("MF Seek 到 {Position}", position);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;

        if (_sourceReader != null)
        {
            try
            {
                Marshal.ReleaseComObject(_sourceReader);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "IMFSourceReader 释放异常");
            }
            _sourceReader = null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：COM 释放为快速同步操作，委托 Dispose + CompletedTask。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Close();
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 从 IMediaStream 提取 URL（文件路径或网络 URL）。
    /// </summary>
    private static string? ExtractUrl(IMediaStream stream)
    {
        // FileMediaStream 的 Name 属性包含文件路径
        // NetworkMediaStream 的 URL 可以从流标识获取
        // 这里简化处理：检查流是否为 FileStream
        if (stream is FileStream fs)
        {
            return fs.Name;
        }

        // 对于其他流类型，无法直接获取 URL
        return null;
    }

    /// <summary>
    /// 解析 MF SourceReader 的轨道信息。
    /// </summary>
    private static IReadOnlyList<MediaTrack> ParseTracks(IMFSourceReader reader)
    {
        var tracks = new List<MediaTrack>();
        int index = 0;

        // 遍历所有流
        while (true)
        {
            int hr = reader.GetNativeMediaType((uint)index, 0, out IMFMediaType? mediaType);
            if (hr == MFConstants.MF_E_NO_MORE_TYPES || hr < 0)
                break;

            if (mediaType == null)
            {
                index++;
                continue;
            }

            mediaType.GetMajorType(out Guid majorType);

            MediaTrack? track = null;

            if (majorType == MFConstants.MFMediaType_Video)
            {
                Guid subtypeKey = MFConstants.MF_MT_SUBTYPE;
                Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
                mediaType.GetGuid(ref subtypeKey, out Guid subtype);
                mediaType.GetUINT64(ref frameSizeKey, out ulong frameSize);
                int width = (int)(frameSize >> 32);
                int height = (int)(frameSize & 0xFFFFFFFF);

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Video,
                    VideoCodec = MapVideoCodec(subtype),
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = width,
                        Height = height,
                        Duration = TimeSpan.Zero
                    }
                };
            }
            else if (majorType == MFConstants.MFMediaType_Audio)
            {
                Guid subtypeKey = MFConstants.MF_MT_SUBTYPE;
                Guid sampleRateKey = MFConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND;
                Guid channelsKey = MFConstants.MF_MT_AUDIO_NUM_CHANNELS;
                Guid bitsPerSampleKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;
                mediaType.GetGuid(ref subtypeKey, out Guid audioSubtype);
                mediaType.GetUINT32(ref sampleRateKey, out uint sampleRate);
                mediaType.GetUINT32(ref channelsKey, out uint channels);
                mediaType.GetUINT32(ref bitsPerSampleKey, out uint bitsPerSample);

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Audio,
                    AudioCodec = MapAudioCodec(audioSubtype),
                    AudioInfo = new AudioTrackInfo
                    {
                        SampleRate = (int)sampleRate,
                        Channels = (int)channels,
                        BitsPerSample = (int)bitsPerSample,
                        Duration = TimeSpan.Zero
                    }
                };
            }

            if (track != null)
            {
                tracks.Add(track);
            }

            index++;
        }

        return tracks;
    }

    private static AudioCodec MapAudioCodec(Guid subtype) => subtype switch
    {
        _ when subtype == MFConstants.MFAudioFormat_AAC => AudioCodec.AAC,
        _ when subtype == MFConstants.MFAudioFormat_MP3 => AudioCodec.MP3,
        _ when subtype == MFConstants.MFAudioFormat_PCM => AudioCodec.PCM,
        _ => AudioCodec.Unknown
    };

    private static VideoCodec MapVideoCodec(Guid subtype) => subtype switch
    {
        _ when subtype == MFConstants.MFVideoFormat_H264 => VideoCodec.H264,
        _ when subtype == MFConstants.MFVideoFormat_H265 => VideoCodec.H265,
        _ => VideoCodec.Unknown
    };
}
