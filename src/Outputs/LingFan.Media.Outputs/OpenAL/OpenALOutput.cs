using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// OpenAL 音频输出（跨平台备选，真实实现）。
/// </summary>
/// <remarks>
/// <para>职责：通过 OpenAL（alc* 设备/上下文 + al* 源/缓冲）播放 PCM 数据，作为跨平台统一回退输出。</para>
/// <para><b>异步策略</b>（与 WASAPI/AAudio 范本一致）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，跨平台无限制，返回 <see cref="Task.CompletedTask"/>。无 I/O 可 await，非伪异步。</item>
/// <item><see cref="Initialize"/>：同步，alcOpenDevice + alcCreateContext + alcMakeContextCurrent + alGenSources。</item>
/// <item><see cref="Submit"/>：同步边界（原生调用），S16/S32/F32 归一到 S16（OpenAL core 通用格式），
/// 动态生成缓冲、队列播放；缓冲满时阻塞（AL_BUFFERS_PROCESSED 轮询背压），同步阻塞背压是正常机制，非伪异步。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步，alSourcePause/Play/Stop + 缓冲出队。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，已消费帧数 / 采样率估算。</item>
/// <item><see cref="Dispose"/>：同步快速释放，停止 + 删缓冲/源 + 销毁上下文 + 关设备。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。</item>
/// </list>
/// <para><b>格式</b>：OpenAL core 保证 8/16-bit 整数；为跨平台确定性，所有输入（S16/S32/F32）归一到
/// <b>S16</b>（F32/S32 经定点缩放），避免依赖 AL_EXT_float32 扩展。音量经 alSourcef(AL_GAIN) 原生增益，无需软件改样。</para>
/// <para><b>所有权</b>：Submit 不接管帧所有权、不 Dispose 帧（规则），调用方负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API <c>[LibraryImport]</c>，零 COM、零反射、零动态代码；原生库经 <see cref="OpenALInterop"/> 运行时解析。</para>
/// <para><b>平台边界</b>：真正跨平台，无 <see cref="PlatformNotSupportedException"/>；宿主须提供 OpenAL 原生库（见 <see cref="OpenALInterop"/>）。</para>
/// <para><b>分部划分</b>：Submit 分部管数据通路（归一入队/背压/缓冲回收）。本文件为设备生命周期、播放控制与时钟。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("android")]
internal sealed unsafe partial class OpenALOutput : IAudioOutput
{
    private IntPtr _device;
    private IntPtr _context;
    private uint _source;

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

    // 主时钟 1× 实时插值（对齐 OpenSlesOutput 同款范式）：_consumedFrames 仅在 Submit 线程背压排空时
    // 步进（一阶一个缓冲时长），直接读值即一个缓冲时长量级的台阶——视频呈现随主时钟台阶"一闪一闪"。
    // 步进锚点 + 单缓冲封顶插值消除阶梯量化；暂停/欠载时封顶使时钟自然冻结，单调性不受影响。
    private long _lastStepQpc;
    private int _lastStepBufferFrames;
    private volatile bool _clockRunning;

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
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _source == 0) return;
        OpenALInterop.alSourcePause(_source);
        _clockRunning = false; // 暂停期间关闭时钟插值（记账值随消费停摆自然冻结）
        Console.Error.WriteLine("[OPENAL-TRACE] Pause");
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _source == 0) return;
        OpenALInterop.alSourcePlay(_source);
        // 重新武装插值：步进锚点重置为当前时刻，避免恢复瞬间读到暂停期累积的陈旧流逝（封顶虽兜底，重置更精确）。
        _clockRunning = true;
        Volatile.Write(ref _lastStepQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        Console.Error.WriteLine("[OPENAL-TRACE] Resume");
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _source == 0) return;

        OpenALInterop.alSourceStop(_source);
        Console.Error.WriteLine("[OPENAL-TRACE] Flush");

        // 出队并删除所有在途缓冲
        OpenALInterop.alGetSourcei(_source, OpenALInterop.AL_BUFFERS_QUEUED, out int queued);
        if (queued > 0)
        {
            DrainProcessedBuffers(queued);
        }

        _submittedFrames = 0;
        _consumedFrames = 0;
        // 步进锚点重置：清空后旧锚点携带的流逝不再有意义
        _clockRunning = false;
        Volatile.Write(ref _lastStepQpc, 0);
        _lastStepBufferFrames = 0;
        // 源保留，下次 Submit 重新播放
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 主时钟 = 已消费帧数/采样率（消费记账，见 Submit 背压）。步进值经 1× 实时插值平滑
    /// （封顶一个缓冲时长，欠载/暂停时自然冻结），消除一个缓冲时长量级的阶梯量化——视频呈现节拍的
    /// "一闪一闪"直接来源于此阶梯。seqlock 式一致性读对齐 OpenSlesOutput 同款模式。
    /// </remarks>
    public TimeSpan GetPlaybackPosition()
    {
        if (!_initialized || _sampleRate <= 0) return TimeSpan.Zero;
        // seqlock 式一致性读：consumed 与 stepQpc 必须来自同一次步进（写序=先 consumed 后 stepQpc）。
        long stepQpcFirst = Volatile.Read(ref _lastStepQpc);
        long consumed = Interlocked.Read(ref _consumedFrames);
        long stepQpc = Volatile.Read(ref _lastStepQpc);
        if (stepQpc != stepQpcFirst)
        {
            consumed = Interlocked.Read(ref _consumedFrames);
            stepQpc = stepQpcFirst;
        }
        long consumedTicks = (long)(consumed * (double)TimeSpan.TicksPerSecond / _sampleRate);
        int bufferFrames = _lastStepBufferFrames;
        if (!_clockRunning || stepQpc == 0 || bufferFrames <= 0)
            return TimeSpan.FromTicks(consumedTicks); // 未步进/暂停：退回纯记账值
        // 插值：距最近一次步进的真实流逝，封顶一个缓冲时长（欠载/停摆时不越过在途音频）。
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(stepQpc).TotalMilliseconds;
        double capMs = bufferFrames * 1000.0 / _sampleRate;
        double interpMs = Math.Clamp(elapsedMs, 0, capMs);
        return TimeSpan.FromTicks(consumedTicks + (long)(interpMs * TimeSpan.TicksPerMillisecond));
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

}
