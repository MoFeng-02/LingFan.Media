using System.Buffers;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// 数据通路分部：Submit 归一入队、AL_BUFFERS_PROCESSED 轮询背压与已消费缓冲回收。
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("android")]
internal sealed unsafe partial class OpenALOutput
{
    /// <summary>最大在途缓冲数（背压上限）：超过则 Submit 阻塞等待消费。</summary>
    private const int MaxInFlightBuffers = 8;

    /// <summary>Submit 背压轮询超时（毫秒）。</summary>
    private const int BackpressureTimeoutMs = 2000;

    // 在途缓冲队列（FIFO，与 OpenAL 出队顺序一致）：缓冲句柄 + 该缓冲帧数。
    private readonly Queue<QueuedBuffer> _pending = new();

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 惰性初始化（与 WasapiRenderLoop 同契约）：FFmpeg/AAC 等场景 OpenAsync 时刻采样率未知
        // （OutputSampleRate=0，见 MediaPlayer.OpenAsync 的显式初始化守卫），首个真实帧携带
        // 采样率/声道数，在此按真实值打开设备。未惰性打开会导致 Submit 无背压秒失败 →
        // 音频管线以解码速度空跑 → 主时钟冲到流末 → 视频帧整段判落后丢弃。
        if (_readyForInit && !_initialized)
            Initialize(frame.SampleRate, frame.Channels);
        if (!_initialized || _source == 0)
            throw new InvalidOperationException("OpenAL 输出尚未初始化，无法提交音频帧。");
        if (frame.Channels != _channels)
            throw new ArgumentException($"音频帧声道数 {frame.Channels} 与输出配置 {_channels} 不匹配。", nameof(frame));

        int frameCount = frame.FrameCount;
        int sampleCount = frameCount * _channels;
        int s16ByteLength = sampleCount * 2; // S16 目标字节数

        // S16 直传；S32/F32 归一到 S16（经租用缓冲）
        if (frame.SampleFormat == SampleFormat.S16)
        {
            fixed (byte* p = frame.Data.Span)
            {
                QueueConvertedFrame(p, s16ByteLength, frameCount);
            }
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(s16ByteLength);
            try
            {
                Span<short> dst = MemoryMarshal.Cast<byte, short>(rented.AsSpan(0, s16ByteLength));
                ConvertToS16(frame.Data.Span, dst, frame.SampleFormat);
                fixed (byte* p = rented)
                {
                    QueueConvertedFrame(p, s16ByteLength, frameCount);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// 将已转换好的 S16 PCM 入队播放（含背压等待 + 动态缓冲生成）。
    /// </summary>
    private void QueueConvertedFrame(byte* pcm, int byteLength, int frameCount)
    {
        // 背压：在途缓冲达上限时，等待至少一个缓冲被消费
        if (_pending.Count >= MaxInFlightBuffers)
        {
            WaitForProcessedBuffers();
        }

        uint buf = 0;
        OpenALInterop.alGenBuffers(1, &buf);
        OpenALInterop.alBufferData(buf, _alFormat, (IntPtr)pcm, byteLength, _sampleRate);
        OpenALInterop.alSourceQueueBuffers(_source, 1, &buf);

        _pending.Enqueue(new QueuedBuffer(buf, frameCount));
        _submittedFrames += frameCount;

        // 源可能因队列耗尽而 STOPPED，确保持续播放
        OpenALInterop.alGetSourcei(_source, OpenALInterop.AL_SOURCE_STATE, out int state);
        if (state != OpenALInterop.AL_PLAYING)
        {
            OpenALInterop.alSourcePlay(_source);
            Console.Error.WriteLine($"[OPENAL-TRACE] alSourcePlay（此前 state=0x{state:X}）");
        }
        _clockRunning = true;
    }

    /// <summary>
    /// 阻塞等待至少一个缓冲被消费（AL_BUFFERS_PROCESSED 轮询背压）。
    /// 出队的缓冲计入 _consumedFrames 并删除，避免泄漏。
    /// </summary>
    private void WaitForProcessedBuffers()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            OpenALInterop.alGetSourcei(_source, OpenALInterop.AL_BUFFERS_PROCESSED, out int processed);
            if (processed > 0)
            {
                DrainProcessedBuffers(processed);
                return;
            }

            if (sw.ElapsedMilliseconds > BackpressureTimeoutMs)
            {
                // 最终检查一次（防误判）
                OpenALInterop.alGetSourcei(_source, OpenALInterop.AL_BUFFERS_PROCESSED, out processed);
                if (processed > 0)
                {
                    DrainProcessedBuffers(processed);
                    return;
                }
                throw new TimeoutException(
                    $"OpenAL 缓冲等待超时（{BackpressureTimeoutMs}ms），音频设备可能已停止或卡死。");
            }

            // 背压轮询（同步阻塞，非伪异步）：OpenAL 为推送式，无事件句柄
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// 出队指定数量已消费缓冲，计入 _consumedFrames 并删除。FIFO 与 _pending 顺序一致。
    /// 消费步进落锚（QPC）供 GetPlaybackPosition 做步进间 1× 实时插值。
    /// </summary>
    private void DrainProcessedBuffers(int count)
    {
        if (count <= 0) return;
        Span<uint> bufArr = count <= 64 ? stackalloc uint[count] : new uint[count];
        fixed (uint* p = bufArr)
        {
            OpenALInterop.alSourceUnqueueBuffers(_source, count, p);
        }

        bool stepped = false;
        for (int i = 0; i < count; i++)
        {
            if (_pending.Count == 0) break;
            var qb = _pending.Dequeue();
            _consumedFrames += qb.Frames;
            _lastStepBufferFrames = qb.Frames;
            stepped = true;
            uint b = qb.Buffer;
            OpenALInterop.alDeleteBuffers(1, &b);
        }
        if (stepped)
            Volatile.Write(ref _lastStepQpc, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>将 S32/F32 PCM 归一到交错 S16（OpenAL core 通用格式）。</summary>
    private static void ConvertToS16(ReadOnlySpan<byte> src, Span<short> dst, SampleFormat format)
    {
        switch (format)
        {
            case SampleFormat.S16:
                MemoryMarshal.Cast<byte, short>(src).CopyTo(dst);
                break;
            case SampleFormat.S32:
                var s32 = MemoryMarshal.Cast<byte, int>(src);
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = (short)(s32[i] >> 16);
                break;
            case SampleFormat.F32:
                var f32 = MemoryMarshal.Cast<byte, float>(src);
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = (short)Math.Clamp(f32[i] * 32768f, -32768f, 32767f);
                break;
            default:
                throw new NotSupportedException($"不支持的采样格式：{format}");
        }
    }

    private readonly struct QueuedBuffer
    {
        public readonly uint Buffer;
        public readonly int Frames;

        public QueuedBuffer(uint buffer, int frames)
        {
            Buffer = buffer;
            Frames = frames;
        }
    }
}