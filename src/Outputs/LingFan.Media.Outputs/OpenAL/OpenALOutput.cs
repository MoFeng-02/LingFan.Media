using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// OpenAL 音频输出（跨平台备选，C 组 AUDIO-STUB 唯一遗留项的真实实现）。
/// </summary>
/// <remarks>
/// <para>职责：通过 OpenAL（alc* 设备/上下文 + al* 源/缓冲）播放 PCM 数据，作为跨平台统一回退输出。</para>
/// <para><b>异步策略</b>（与 WASAPI/AAudio 范本一致，遵守总记忆第十二章）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，跨平台无限制，返回 <see cref="Task.CompletedTask"/>。无 I/O 可 await，非伪异步。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），alcOpenDevice + alcCreateContext + alcMakeContextCurrent + alGenSources。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），S16/S32/F32 归一到 S16（OpenAL core 通用格式），
/// 动态生成缓冲、队列播放；缓冲满时阻塞（AL_BUFFERS_PROCESSED 轮询背压），同步阻塞背压是正常机制，非伪异步。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），alSourcePause/Play/Stop + 缓冲出队。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，已消费帧数 / 采样率估算。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），停止 + 删缓冲/源 + 销毁上下文 + 关设备。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。</item>
/// </list>
/// <para><b>格式</b>：OpenAL core 保证 8/16-bit 整数；为跨平台确定性，所有输入（S16/S32/F32）归一到
/// <b>S16</b>（F32/S32 经定点缩放），避免依赖 AL_EXT_float32 扩展。音量经 alSourcef(AL_GAIN) 原生增益，无需软件改样。</para>
/// <para><b>所有权</b>：Submit 不接管帧所有权、不 Dispose 帧（规则），调用方负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API <see cref="LibraryImport"/>，零 COM、零反射、零动态代码；原生库经 <see cref="OpenALInterop"/> 运行时解析。</para>
/// <para><b>平台边界</b>：真正跨平台，无 <see cref="PlatformNotSupportedException"/>；宿主须提供 OpenAL 原生库（见 <see cref="OpenALInterop"/>）。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("android")]
internal sealed unsafe class OpenALOutput : IAudioOutput
{
    /// <summary>最大在途缓冲数（背压上限）：超过则 Submit 阻塞等待消费。</summary>
    private const int MaxInFlightBuffers = 8;

    /// <summary>Submit 背压轮询超时（毫秒）。</summary>
    private const int BackpressureTimeoutMs = 2000;

    private IntPtr _device;
    private IntPtr _context;
    private uint _source;

    // 在途缓冲队列（FIFO，与 OpenAL 出队顺序一致）：缓冲句柄 + 该缓冲帧数。
    private readonly Queue<QueuedBuffer> _pending = new();

    private bool _readyForInit;
    private bool _initialized;
    private bool _disposed;
    private int _sampleRate;
    private int _channels;
    private int _alFormat;       // AL_FORMAT_MONO16 / STEREO16
    private float _volume = 1.0f;

    // 播放位置估算（帧计数，非线程安全，由音频线程独占访问）
    private long _submittedFrames;
    private long _consumedFrames;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (_readyForInit)
            throw new InvalidOperationException("OpenAL 输出已初始化，请勿重复调用 InitializeAsync。");
        _readyForInit = true;
        return Task.CompletedTask; // 契约方法：跨平台无 I/O await，非伪异步
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_readyForInit)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");
        if (_initialized)
            throw new InvalidOperationException("OpenAL 输出已初始化，请先 Dispose 再重新初始化。");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        _sampleRate = sampleRate;
        _channels = channels;
        _alFormat = channels > 1 ? OpenALInterop.AL_FORMAT_STEREO16 : OpenALInterop.AL_FORMAT_MONO16;

        // 1. 打开默认设备
        _device = OpenALInterop.alcOpenDevice(null);
        if (_device == IntPtr.Zero)
            throw new InvalidOperationException("alcOpenDevice 失败：无法打开默认 OpenAL 设备（原生库是否安装？）。");

        // 2. 创建并激活上下文（指定采样率）
        int freq = sampleRate;
        int* attrs = stackalloc int[]
        {
            OpenALInterop.ALC_FREQUENCY, freq,
            0 // 属性列表结束
        };
        _context = OpenALInterop.alcCreateContext(_device, attrs);
        if (_context == IntPtr.Zero)
        {
            OpenALInterop.alcCloseDevice(_device);
            _device = IntPtr.Zero;
            throw new InvalidOperationException("alcCreateContext 失败：无法创建 OpenAL 上下文。");
        }

        if (!OpenALInterop.alcMakeContextCurrent(_context))
        {
            OpenALInterop.alcDestroyContext(_context);
            OpenALInterop.alcCloseDevice(_device);
            _context = IntPtr.Zero;
            _device = IntPtr.Zero;
            throw new InvalidOperationException("alcMakeContextCurrent 失败：无法激活 OpenAL 上下文。");
        }

        // 3. 创建源并设置初始音量（原生增益，无需软件改样）
        uint src = 0;
        OpenALInterop.alGenSources(1, &src);
        _source = src;
        OpenALInterop.alSourcef(_source, OpenALInterop.AL_GAIN, _volume);

        _initialized = true;
    }

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        }
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
    /// </summary>
    private void DrainProcessedBuffers(int count)
    {
        if (count <= 0) return;
        Span<uint> bufArr = count <= 64 ? stackalloc uint[count] : new uint[count];
        fixed (uint* p = bufArr)
        {
            OpenALInterop.alSourceUnqueueBuffers(_source, count, p);
        }

        for (int i = 0; i < count; i++)
        {
            if (_pending.Count == 0) break;
            var qb = _pending.Dequeue();
            _consumedFrames += qb.Frames;
            uint b = qb.Buffer;
            OpenALInterop.alDeleteBuffers(1, &b);
        }
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

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _source == 0) return;
        OpenALInterop.alSourcePause(_source);
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _source == 0) return;
        OpenALInterop.alSourcePlay(_source);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _source == 0) return;

        OpenALInterop.alSourceStop(_source);

        // 出队并删除所有在途缓冲
        OpenALInterop.alGetSourcei(_source, OpenALInterop.AL_BUFFERS_QUEUED, out int queued);
        if (queued > 0)
        {
            DrainProcessedBuffers(queued);
        }

        _submittedFrames = 0;
        _consumedFrames = 0;
        // 源保留，下次 Submit 重新播放
    }

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition()
    {
        if (!_initialized || _sampleRate <= 0) return TimeSpan.Zero;
        long consumed = _consumedFrames;
        return consumed <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)consumed / _sampleRate);
    }

    /// <inheritdoc/>
    public TimeSpan Latency
    {
        get
        {
            if (!_initialized || _sampleRate <= 0) return TimeSpan.Zero;
            long remaining = _submittedFrames - _consumedFrames;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)remaining / _sampleRate);
        }
    }

    /// <inheritdoc/>
    public float Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float clamped = Math.Clamp(value, 0.0f, 1.0f);
            _volume = clamped;
            if (_initialized && _source != 0)
            {
                // 原生增益，无需软件改样
                OpenALInterop.alSourcef(_source, OpenALInterop.AL_GAIN, clamped);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_source != 0)
        {
            try
            {
                OpenALInterop.alSourceStop(_source);
                OpenALInterop.alGetSourcei(_source, OpenALInterop.AL_BUFFERS_QUEUED, out int queued);
                if (queued > 0)
                {
                    Span<uint> bufArr = queued <= 64 ? stackalloc uint[queued] : new uint[queued];
                    fixed (uint* p = bufArr)
                    {
                        OpenALInterop.alSourceUnqueueBuffers(_source, queued, p);
                        OpenALInterop.alDeleteBuffers(queued, p);
                    }
                }
                uint src = _source;
                OpenALInterop.alDeleteSources(1, &src);
            }
            catch { /* 忽略释放错误 */ }
            _source = 0;
        }

        if (_context != IntPtr.Zero)
        {
            OpenALInterop.alcMakeContextCurrent(IntPtr.Zero);
            OpenALInterop.alcDestroyContext(_context);
            _context = IntPtr.Zero;
        }

        if (_device != IntPtr.Zero)
        {
            OpenALInterop.alcCloseDevice(_device);
            _device = IntPtr.Zero;
        }

        _pending.Clear();
        _initialized = false;
        _readyForInit = false;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask; // 契约方法：无 I/O 可 await，非伪异步
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
