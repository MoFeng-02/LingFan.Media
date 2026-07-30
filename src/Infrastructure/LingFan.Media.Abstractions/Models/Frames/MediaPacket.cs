namespace LingFan.Media.Abstractions;

/// <summary>
/// 解封装后的压缩数据包。实现 <see cref="IDisposable"/>。
/// </summary>
/// <remarks>
/// <para>内存安全注释（V2-05 引用计数零拷贝）：</para>
/// <list type="bullet">
/// <item>零拷贝路径：FFmpegDemuxer 通过 av_packet_clone 引用计数共享 FFmpeg 内部 buffer，
/// <see cref="Data"/> 直接映射原生内存，所有者以中立 <see cref="IDisposable"/> 传入</item>
/// <item>拷贝路径（无 dataOwner）：MediaPacket 独立拥有托管数据副本（兼容非引用计数来源）</item>
/// <item><see cref="Dispose"/> 释放所有者（原生引用计数减一）；释放后不得再访问 <see cref="Data"/></item>
/// </list>
/// </remarks>
public sealed class MediaPacket : IDisposable
{
    /// <summary>所属轨道索引。</summary>
    public int TrackIndex { get; }

    /// <summary>压缩数据（只读视图，底层 buffer 由 packet 拥有）。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>时间戳（PTS）。</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>数据包持续时间。</summary>
    public TimeSpan Duration { get; }

    /// <summary>是否关键帧。</summary>
    public bool KeyFrame { get; }

    // ── 已解码（直通）packet 的真实帧参数 ──
    // 仅 VLC 等"解封装+解码一体"的直通后端会把已解码数据放入 MediaPacket，
    // 此时这些字段携带真实帧格式/尺寸；压缩 packet（FFmpeg/MF）保持默认 0/default，不参与解包。
    /// <summary>解码帧宽度（仅直通 packet 有意义，压缩 packet 为 0）。</summary>
    public int Width { get; }

    /// <summary>解码帧高度（压缩 packet 为 0）。</summary>
    public int Height { get; }

    /// <summary>解码帧行跨度字节数（压缩 packet 为 0）。</summary>
    public int Stride { get; }

    /// <summary>音频采样率（压缩 packet 为 0）。</summary>
    public int SampleRate { get; }

    /// <summary>音频声道数（压缩 packet 为 0）。</summary>
    public int Channels { get; }

    /// <summary>音频采样格式（压缩 packet 为 default）。</summary>
    public SampleFormat Format { get; }

    private IDisposable? _dataOwner;
    private bool _disposed;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 初始化 <see cref="MediaPacket"/> 的新实例。
    /// </summary>
    /// <param name="trackIndex">所属轨道索引。</param>
    /// <param name="data">压缩数据（托管副本或零拷贝原生视图）。</param>
    /// <param name="timestamp">时间戳（PTS）。</param>
    /// <param name="duration">数据包持续时间。</param>
    /// <param name="keyFrame">是否关键帧。</param>
    /// <param name="dataOwner">
    /// V2-05 零拷贝所有者（可选）。非 null 时 <paramref name="data"/> 映射原生引用计数 buffer，
    /// <see cref="Dispose"/> 释放该所有者使原生引用计数减一。
    /// </param>
    /// <param name="width">解码帧宽度（直通 packet 用，默认 0）。</param>
    /// <param name="height">解码帧高度（直通 packet 用，默认 0）。</param>
    /// <param name="stride">解码帧行跨度字节数（直通 packet 用，默认 0）。</param>
    /// <param name="sampleRate">音频采样率（直通 packet 用，默认 0）。</param>
    /// <param name="channels">音频声道数（直通 packet 用，默认 0）。</param>
    /// <param name="format">音频采样格式（直通 packet 用，默认 default）。</param>
    public MediaPacket(int trackIndex, ReadOnlyMemory<byte> data,
        TimeSpan timestamp, TimeSpan duration, bool keyFrame,
        IDisposable? dataOwner = null,
        int width = 0, int height = 0, int stride = 0,
        int sampleRate = 0, int channels = 0, SampleFormat format = default)
    {
        TrackIndex = trackIndex;
        Data = data;
        Timestamp = timestamp;
        Duration = duration;
        KeyFrame = keyFrame;
        _dataOwner = dataOwner;
        Width = width;
        Height = height;
        Stride = stride;
        SampleRate = sampleRate;
        Channels = channels;
        Format = format;
    }

    /// <summary>释放底层 buffer（零拷贝路径：原生引用计数减一）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_dataOwner != null)
        {
            _dataOwner.Dispose();
            _dataOwner = null;
        }
    }
}
