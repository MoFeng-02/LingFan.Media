using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LingFan.Media.Backends.VLCNative.Demuxer;

// VLCNativeDemuxer 的 Callbacks 职责段（部分类，由 VLCNativeDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Close/Dispose）在主文件；VLC 回调式帧交付适配为拉取式接口。
internal sealed partial class VLCNativeDemuxer
{
    private uint OnVideoFormat(IntPtr opaque, IntPtr chroma, IntPtr width, IntPtr height, IntPtr pitches, IntPtr lines)
    {
        _videoFormatReceived = true; // 视频轨存在的权威信号（弥补播放前 tracks_get 漏列）
        // VLC 期望通过 chroma 指针写入 FourCC
        Marshal.WriteInt32(chroma, (int)VlcCodecMapping.FourCC("BGRA"));
        uint w = (uint)Marshal.ReadInt32(width);
        uint h = (uint)Marshal.ReadInt32(height);
        uint pitch = w * 4;
        Marshal.WriteInt32(pitches, (int)pitch);
        Marshal.WriteInt32(lines, (int)h);

        _videoWidth = (int)w;
        _videoHeight = (int)h;
        _videoPitch = (int)pitch;
        _videoPtsAnchorStarted = false; // 新格式/新流起点：重置 PTS 墙钟锚点
        RestartAudioRelease(); // 同步重置音频释放实时速率锚点（同生命周期）

        if (_videoBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_videoBuffer);
        _videoBuffer = Marshal.AllocHGlobal((int)(pitch * h));

        return 1;
    }

    private void OnVideoCleanup(IntPtr opaque)
    {
        if (_videoBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = IntPtr.Zero;
        }
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        if (_videoBuffer != IntPtr.Zero)
            Marshal.WriteIntPtr(planes, _videoBuffer);
        return IntPtr.Zero;
    }

    private void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        if (_videoBuffer == IntPtr.Zero || _videoTrackIndex < 0) return;

        int dataSize = _videoPitch * _videoHeight;
        byte[] data = new byte[dataSize];
        Marshal.Copy(_videoBuffer, data, 0, dataSize);

        // 视频帧 PTS：纯 CFR 计数（frame_counter × 单帧时长）不跟真实时钟对齐，
        // VLC 软解经 libvlc 回调突发/抖动投递时，帧实际到达墙钟常落后合成 PTS 超过 DropThreshold
        // → 被 Synchronizer 判「严重落后」丢弃。
        // 故以播放起始墙钟为锚、按真实投递时刻合成 PTS，并对齐帧最小间距：
        //   突发（preroll 多帧同刻到达）→ 取「上一 PTS + 单帧时长」保持正确帧序与间距；
        //   平稳/抖动迟到 → 取「墙钟时刻」使 PTS 跟随主时钟、消除落后丢帧。
        if (!_videoPtsAnchorStarted)
        {
            _videoPtsAnchorStarted = true;
            _videoPtsAnchorTimestamp = Stopwatch.GetTimestamp();
            _lastVideoPtsTicks = _ptsBaseTicks;
        }
        long elapsedTicks = Stopwatch.GetElapsedTime(_videoPtsAnchorTimestamp).Ticks;
        long candidateTicks = _ptsBaseTicks + elapsedTicks;
        long ptsTicks = candidateTicks > _lastVideoPtsTicks + _videoFrameDurationTicks
            ? candidateTicks
            : _lastVideoPtsTicks + _videoFrameDurationTicks;
        _lastVideoPtsTicks = ptsTicks;
        var pts = TimeSpan.FromTicks(ptsTicks);

        var packet = new MediaPacket(
            _videoTrackIndex, data,
            pts,
            TimeSpan.Zero, keyFrame: true,
            width: _videoWidth, height: _videoHeight, stride: _videoPitch);

        _videoChannel.Writer.TryWrite(packet);
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture) { }

    private int OnAudioSetup(IntPtr opaque, IntPtr format, IntPtr rate, IntPtr channels)
    {
        _audioSetupReceived = true; // 音频轨存在的权威信号
        // 关键 ABI 约束：format 原生是 char*（按值），绝不可作为 ref IntPtr 读写（栈破坏/野指针崩溃）。
        // 按 VLC 默认 S16N 消费（已用 :amem-format=s16l 强制）。
        _audioSampleRate = (int)Marshal.ReadInt32(rate);
        _audioChannels = (int)Marshal.ReadInt32(channels);
        _audioBytesPerSample = 2;            // S16N：16 位有符号，每样本 2 字节
        _audioSampleFormat = SampleFormat.S16;
        _audioSampleCounter = 0;
        _logger.LogInformation("VLC amem 音频格式协商: rate={Rate}Hz channels={Channels}（已强制 S16N；消费侧固定 2 字节/样本）",
            _audioSampleRate, _audioChannels);
        return 0;
    }

    private void OnAudioCleanup(IntPtr opaque) { }

    private void OnAudioPause(IntPtr data, long pts) { }

    private void OnAudioResume(IntPtr data, long pts) { }

    private void OnAudioFlush(IntPtr data, long pts)
    {
        // 诊断（定位剩余音频间隙的原因）：正常播放中途若触发，说明 VLC 主动丢弃已解码音频，
        // 会清空抖动缓冲与通道内音频、造成真实断音。若重跑仍见间隙且此日志在起播阶段出现，则原因是 VLC flush 而非节流。
        int cleared = 0;
        while (_audioJitter.TryDequeue(out var stale)) { stale.Dispose(); cleared++; }
        if (cleared > 0)
            _logger.LogWarning("[VLC-AUDIO] OnAudioFlush 触发：抖动缓冲清空 {Count} 包（中途断音诊断）", cleared);
        else
            _logger.LogDebug("[VLC-AUDIO] OnAudioFlush 触发：抖动缓冲已空（seek/启动常规路径）");
        // 清空音频通道内滞留音频：flush 后旧音频不可再释放，避免 seek 后串音。
        // 音频已独立通道，不会被视频 DropOldest 误删；视频通道不干预（保持原语义：仅清音频，视频帧后续自然接续）。
        int audioCleared = 0;
        while (_audioChannel.Reader.TryRead(out var staleAudio)) { staleAudio.Dispose(); audioCleared++; }
        if (audioCleared > 0)
            _logger.LogDebug("[VLC-AUDIO] OnAudioFlush 已丢弃通道内滞留音频 {Count} 包", audioCleared);
    }

    private void OnAudioDrain(IntPtr data) { }

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_audioTrackIndex < 0 || _audioSampleRate <= 0) return;

        int dataSize = (int)count * _audioChannels * _audioBytesPerSample;
        byte[] audioData = new byte[dataSize];
        Marshal.Copy(samples, audioData, 0, dataSize);

        // 先取值后累加：首包 PTS = _ptsBaseTicks（常态 0），与视频首帧同起点。
        var timestamp = TimeSpan.FromTicks(
            _ptsBaseTicks + _audioSampleCounter * TimeSpan.TicksPerSecond / _audioSampleRate);
        _audioSampleCounter += count;

        var packet = new MediaPacket(
            _audioTrackIndex, audioData,
            timestamp,
            TimeSpan.Zero, keyFrame: true,
            sampleRate: _audioSampleRate, channels: _audioChannels, format: _audioSampleFormat);

        // 入队抖动缓冲（生产者）：释放线程按实时速率稳定释放到共享通道，吸收 VLC amem 突发投递，
        // 消除 WASAPI 渲染侧欠载间隙。超上限丢弃最旧音频（瞬断劣于长卡，防失控增长）。
        while (_audioJitter.Count >= AudioJitterMaxPackets && _audioJitter.TryDequeue(out var old)) old.Dispose();
        _audioJitter.Enqueue(packet);
        EnsureAudioReleaseStarted();
    }
}
