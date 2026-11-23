using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Outputs.OpenSLES;

/// <summary>
/// OpenSL ES 音频输出（Android），平台扩展。
/// </summary>
/// <remarks>
/// <para>职责：通过 Android NDK OpenSL ES API 直接播放 PCM 数据（libOpenSLES.so）。
/// 作为 AAudio 的低版本回退（API &lt; 27 无 AAudio）。</para>
/// <para><b>异步策略</b>（与 WASAPI 范本一致）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，同步执行引擎/混音/播放器创建后返回 <see cref="Task.CompletedTask"/>。
/// 全部为 NDK 同步原生调用，无 I/O 可 await，<b>非伪异步</b>（不加 <c>async</c> 关键字、方法体无 <c>await</c>）。</item>
/// <item><see cref="Initialize"/>：同步，构建 PCM 格式/数据源/数据汇并创建播放器。</item>
/// <item><see cref="Submit"/>：同步边界（原生调用），拷贝 PCM 到原生缓冲并 <c>Enqueue</c>；缓冲满时由缓冲队列计数做背压（阻塞等待）。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步，SetPlayState(PAUSED/PLAYING)/BufferQueue.Clear。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，主时钟消费记账（首帧 PTS 锚点 + 已完成缓冲采样数），
/// 等价 AudioTrack.playbackHeadPosition 语义；<see cref="GetPlaybackPositionDirect"/> 默认转发至此。
/// 不读 SLPlayItf::GetPosition（厂商设备半冻结值不可信）。</item>
/// <item><see cref="Dispose"/>：同步快速释放，Destroy 所有 NDK 对象。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。</item>
/// </list>
/// <para><b>线程安全</b>：Submit 在音频管线线程调用；Pause/Resume/Flush 在控制线程调用，不可并发。
/// 缓冲队列回调在 OpenSL ES 内部线程触发，通过 <see cref="SemaphoreSlim"/> 与缓冲池与 Submit 同步。</para>
/// <para><b>所有权</b>：Submit 不接管帧所有权、不 Dispose 帧（规则），调用方负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类；NDK 互操作走 <c>LibraryImport</c> + 方法表 vtable 委托封送（<c>GetDelegateForFunctionPointer</c>），
/// 零反射；IID 经 <c>NativeLibrary.TryGetExport</c> + <c>Marshal.ReadIntPtr</c> 取得，无动态代码。</para>
/// <para><b>平台边界</b>：仅 Android 有效；非 Android 调用抛 <see cref="PlatformNotSupportedException"/>。编译期跨平台可编译。</para>
/// </remarks>
[SupportedOSPlatform("Android")]
internal sealed unsafe partial class OpenSlesOutput : IAudioOutput
{
    private readonly ILogger<OpenSlesOutput> _logger;

    public OpenSlesOutput(ILogger<OpenSlesOutput> logger)
    {
        _logger = logger;
    }

    // OpenSL ES 常量（NDK OpenSLES.h）
    private const int SL_RESULT_SUCCESS = 0;
    private const uint SL_BOOLEAN_FALSE = 0; // SLboolean = SLuint32（32 位，OpenSLES.h:73）
    private const uint SL_BOOLEAN_TRUE = 1;
    private const uint SL_DATAFORMAT_PCM = 0x00000002; // NDK OpenSLES.h:308（Android 头 PCM=2、MIME=1，与 Khronos 规范相反；误写 1 会被当 MIME 解析）
    private const uint SL_DATALOCATOR_ANDROIDSIMPLEBUFFERQUEUE = 0x800007BD;
    private const uint SL_DATALOCATOR_OUTPUTMIX = 0x00000004;
    private const uint SL_SAMPLINGRATE_44_1 = 44100000; // milliHz
    private const uint SL_SAMPLINGRATE_48 = 48000000;
    private const uint SL_PCMSAMPLEFORMAT_FIXED_8 = 8;
    private const uint SL_PCMSAMPLEFORMAT_FIXED_16 = 16;
    private const uint SL_PCMSAMPLEFORMAT_FIXED_32 = 32;
    private const uint SL_SPEAKER_FRONT_LEFT = 0x00000001;
    private const uint SL_SPEAKER_FRONT_RIGHT = 0x00000002;
    private const uint SL_BYTEORDER_LITTLEENDIAN = 0x00000002; // NDK OpenSLES.h:321（BIGENDIAN=1、LITTLEENDIAN=2；误写 1 报 "unsupported byte order 1"）
    private const uint SL_PLAYSTATE_STOPPED = 0x00000001; // NDK OpenSLES.h（STOPPED=1、PAUSED=2、PLAYING=3）
    private const uint SL_PLAYSTATE_PAUSED = 0x00000002;
    private const uint SL_PLAYSTATE_PLAYING = 0x00000003;

    // vtable 槽位（对照本机 NDK OpenSLES.h 权威布局，勿凭记忆）
    // SLObjectItf_: Realize(0) Resume(1) GetState(2) GetInterface(3) RegisterCallback(4)
    //               AbortAsyncOperation(5) Destroy(6) SetPriority(7) ...
    // SLEngineItf_: CreateLEDDevice(0) CreateVibraDevice(1) CreateAudioPlayer(2)
    //               CreateAudioRecorder(3) CreateMidiPlayer(4) CreateListener(5)
    //               Create3DGroup(6) CreateOutputMix(7) CreateMetadataExtractor(8) ...
    private const int SLOT_Object_Realize = 0;
    private const int SLOT_Object_GetInterface = 3;
    private const int SLOT_Object_Destroy = 6;
    private const int SLOT_Engine_CreateAudioPlayer = 2;
    private const int SLOT_Engine_CreateOutputMix = 7;
    private const int SLOT_Play_SetPlayState = 0;
    private const int SLOT_BQ_Enqueue = 0;
    private const int SLOT_BQ_Clear = 1;
    private const int SLOT_BQ_RegisterCallback = 3;
    private const int SLOT_Volume_SetVolumeLevel = 0;

    // 在途缓冲深度：设备 BufferQueue 水库 = MaxInFlightBuffers × 单缓冲时长。
    // 水库须覆盖音频解码阶段的零提交窗口（解码输出轮询等待 + JNI 开销）与 CPU 调度抖动，
    // 消除周期性欠载（音频咔哒 + 主时钟停走 = 帧卡顿成因）。加深水库仅增加等量
    // 常驻内存与呈现延迟，对 A/V 同步无影响（视频按 master 呈现）。
    private const int MaxInFlightBuffers = 8;

    // NDK 对象（SLObjectItf = void**，以 IntPtr 持有方法表指针）
    private IntPtr _engineObject;
    private IntPtr _outputMixObject;
    private IntPtr _playerObject;
    private IntPtr _engineItf;
    private IntPtr _playItf;
    private IntPtr _bqItf;
    private IntPtr _volumeItf;

    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;
    private int _sampleRate;
    private int _channels;
    private float _volume = 1.0f;

    // 缓冲池 + 背压信号
    private readonly ConcurrentQueue<IntPtr> _freeBuffers = new();
    private readonly ConcurrentQueue<IntPtr> _inFlightBuffers = new();
    private readonly SemaphoreSlim _backpressure = new(MaxInFlightBuffers, MaxInFlightBuffers);
    private GCHandle _thisHandle;
    private BufferQueueCallback? _bqCallback; // 保持存活，防止 GC

    // 主时钟记账（ExoPlayer AudioSink 范式：消费帧数 + 首帧媒体 PTS 锚点）
    // SLPlayItf::GetPosition 在部分厂商设备上返回半冻结值：间歇小幅推进后
    // 长时间停滞 → 主时钟推进速率 ≪ 墙钟 → 视频帧永远差"临门一脚"无法呈现；且停摆看门狗
    // （推进过小判停摆）被间歇推进欺骗 → 全链路冻结（画面停首帧、音频照常播完）。
    // 改为自身记账（等价 AudioTrack.playbackHeadPosition + 锚点）：设备无关、确定单调 1× 实时，
    // 暂停自然冻结、欠载自然停摆——这正是主时钟需要的全部性质。
    private long _framesConsumed;                    // 已播完的采样数（Interlocked）
    private long _anchorMediaTicks = long.MinValue;  // 首个提交帧的媒体 PTS（哨兵=尚未提交）
    private readonly ConcurrentQueue<int> _inFlightSamples = new(); // 与 _inFlightBuffers 严格同序的每缓冲采样数

    // 记账时钟平滑插值：消费步进随缓冲完成回调发生（阶梯信号），视频呈现时刻若直接消费
    // 该阶梯值，会被量化到回调边界（±一个缓冲时长），呈现节拍随之抖动。插值 = 步进值 +
    // 距最近一次步进的真实流逝（1× 实时），并封顶一个缓冲时长：欠载/回调停摆时时钟冻结在
    // "已播完 + 至多一个在途缓冲"，绝不越过实际可闻位置漂移。暂停期间插值关闭。
    private long _lastStepQpc;      // 最近一次消费步进时刻（Volatile 读写；0=尚未步进）
    private int _lastBufferSamples; // 最近一个完成缓冲的采样数（插值封顶用）

    // 节拍仪表（欠载定位）：完成回调到达间隔 ≈ 单缓冲时长为健康（队列常满，mixer 1× 消费）；
    // 间隔超出 capMs 的部分 = 设备 BufferQueue 排空时间（喂帧跟不上）或回调调度延迟。
    // 窗口欠载占比应与 [SYNC] 快照的「1 - 时钟比」吻合，用于区分「喂帧被饿」与「回调调度延迟」。
    // 全部字段在回调线程 _gate 内读写（步进与状态输出同处持锁），零额外锁开销。
    private long _pacePrevStepQpc;      // 上一次步进时刻（0=无前值，跳过首个间隔）
    private long _paceWindowStartQpc;   // 状态窗口起点（5s 滚动；0=未开窗）
    private int _paceStepCount;         // 窗口内回调步进数
    private double _paceIntervalSumMs;  // 窗口内回调间隔和（均值分母 = 步进数）
    private double _paceIntervalPeakMs; // 窗口内回调间隔峰值
    private int _underrunCount;         // 窗口内欠载事件数（间隔 > 1.25×capMs）
    private double _underrunDebtMs;     // 窗口内欠载累计时长（间隔超出 capMs 的部分）
    private int _paceInFlightMin = int.MaxValue; // 窗口内在途缓冲最小值：=0 即设备完全排空（喂帧被饿实锤）；
                                                 // >0 而间隔仍拉伸 → 回调调度延迟路径
    private volatile bool _clockRunning; // 播放中（Resume 置位；Pause/重播复位清除）

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_IntPtrDelegate(IntPtr self, IntPtr iid, out IntPtr pInterface);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_OutObjDelegate(IntPtr self, out IntPtr pObj, uint numInterfaces, IntPtr ids, IntPtr req);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_SourceSinkDelegate(IntPtr self, out IntPtr pObj, IntPtr pSource, IntPtr pSink, uint numInterfaces, IntPtr ids, IntPtr req);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_VoidDelegate(IntPtr self); // SLresult (*)(self)，如 BufferQueue.Clear

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_BoolDelegate(IntPtr self, uint asyncFlag); // SLresult (*Realize)(self, SLboolean=SLuint32)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Void_SelfDelegate(IntPtr self); // void (*Destroy)(self)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_UintDelegate(IntPtr self, uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_ShortDelegate(IntPtr self, short value); // SLresult (*SetVolumeLevel)(self, SLmillibel/*SLint16*/)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_EnqueueDelegate(IntPtr self, IntPtr buffer, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SLResult_RegisterCallbackDelegate(IntPtr self, BufferQueueCallback callback, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BufferQueueCallback(IntPtr bq, IntPtr context);

    // libOpenSLES P/Invoke
    [LibraryImport("libOpenSLES.so")]
    private static partial int slCreateEngine(out IntPtr pEngine, uint numOptions, IntPtr pEngineOptions, uint numInterfaces, IntPtr pInterfaces, IntPtr pInterfaceRequired);

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfNotAndroid();
        if (_engineObject != IntPtr.Zero)
            throw new InvalidOperationException("OpenSL ES 输出已初始化，请勿重复调用 InitializeAsync。");
        InitializeCore();
        return Task.CompletedTask; // 契约方法：无真实 I/O await，非伪异步
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _playItf == IntPtr.Zero) return;
        var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
        // PAUSED 保留播放头（STOPPED 会把 GetPosition 归零 → 恢复后主时钟与媒体时间错位、画面冻结）。
        setState(_playItf, SL_PLAYSTATE_PAUSED);
        _clockRunning = false; // 暂停期间关闭时钟插值（记账值本身随回调停摆自然冻结）
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _playItf == IntPtr.Zero) return;
        var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
        setState(_playItf, SL_PLAYSTATE_PLAYING);
        // 重新武装插值：步进锚点重置为当前时刻，避免恢复瞬间读到暂停期累积的陈旧流逝
        // （封顶虽兜底 ≤ 一个缓冲，重置更精确）。
        _clockRunning = true;
        Volatile.Write(ref _lastStepQpc, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _bqItf == IntPtr.Zero) return;
        // 全程持 _gate：Clear + 排空 + 背压释放必须与完成回调（持 _gate Release）互斥——
        // 否则 CurrentCount 读取与回调 Release 并发可超发票数 → 后续 Submit 撞
        // SL_RESULT_BUFFER_INSUFFICIENT 丢帧 + consumed 缺计。
        lock (_gate)
        {
            var clear = GetVTable<SLResult_VoidDelegate>(_bqItf, SLOT_BQ_Clear);
            clear(_bqItf);
            // 清空在途缓冲回到空闲池（被清缓冲未播完，不计入主时钟消费）
            int drained = 0;
            while (_inFlightBuffers.TryDequeue(out IntPtr buf))
            {
                _freeBuffers.Enqueue(buf);
                drained++;
            }
            _inFlightSamples.Clear();
            // 背压对账：按实际排空数释放（每个在途缓冲恰好对应一次获取），消除超发窗口。
            if (drained > 0)
                _backpressure.Release(drained);
            // 步进锚点重置：清空后在途深度归零，旧锚点携带的流逝不再有意义（封顶虽兜底 ≤ 一个缓冲，重置更精确）。
            Volatile.Write(ref _lastStepQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 主时钟 = 首帧媒体 PTS 锚点 + 已消费采样数/采样率（消费记账，等价 AudioTrack.playbackHeadPosition）。
    /// 不读 SLPlayItf::GetPosition——该 API 在部分厂商设备上返回半冻结值，
    /// 若读取它，主时钟推进会 ≪ 墙钟 → 视频帧永久 Wait、画面冻结而音频照常（详见字段区注释）。
    /// 记账时钟的性质：消费严格 1× 实时（单调）、暂停自然冻结、欠载自然停摆——主时钟所需全部性质。
    /// 步进值经 1× 实时插值平滑（封顶一个缓冲时长，见字段区注释），消除呈现时刻的阶梯量化；
    /// 暂停/欠载时封顶使时钟自然冻结，单调性不受影响。
    /// 线程安全：Interlocked/Volatile 读写，视频管线线程高频轮询无锁。
    /// </remarks>
    public TimeSpan GetPlaybackPosition()
    {
        long anchor = Interlocked.Read(ref _anchorMediaTicks);
        if (anchor == long.MinValue || _sampleRate <= 0)
            return TimeSpan.Zero; // 尚无音频提交：主时钟保持 0（视频首帧门控期内）
        // seqlock 式一致性读：consumed 与 stepQpc 必须来自同一次步进——步进落在两次读取间隙
        // 会使插值在新 consumed 上叠加至多一个缓冲时长的锯齿（呈现时刻抖动的直接来源）。
        // 写序（回调线程）= 先 consumed 后 stepQpc，故两次 stepQpc 相同即 consumed 一致。
        long stepQpcFirst = Volatile.Read(ref _lastStepQpc);
        long consumed = Interlocked.Read(ref _framesConsumed);
        long stepQpc = Volatile.Read(ref _lastStepQpc);
        if (stepQpc != stepQpcFirst)
        {
            consumed = Interlocked.Read(ref _framesConsumed);
            stepQpc = stepQpcFirst;
        }
        long consumedTicks = (long)(consumed * (double)TimeSpan.TicksPerSecond / _sampleRate);
        long baseTicks = anchor + consumedTicks;
        int bufferSamples = _lastBufferSamples;
        if (!_clockRunning || stepQpc == 0 || bufferSamples <= 0)
            return TimeSpan.FromTicks(baseTicks); // 未步进/暂停：退回纯记账值（行为与插值前一致）
        // 插值：距最近一次步进的真实流逝，封顶一个缓冲时长（欠载/回调停摆时不越过在途音频）。
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(stepQpc).TotalMilliseconds;
        double capMs = bufferSamples * 1000.0 / _sampleRate;
        double interpMs = Math.Clamp(elapsedMs, 0, capMs);
        return TimeSpan.FromTicks(baseTicks + (long)(interpMs * TimeSpan.TicksPerMillisecond));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 重播（Ended→Playing）主时钟归零：仅重置记账（不动设备状态——SetPlayState(STOPPED) 会重绕
    /// BufferQueue 播放头导致残留缓冲重放爆音）。残留缓冲的完成回调因样本队列已清、不再计入新周期。
    /// </remarks>
    public void ResetPlaybackClock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref _anchorMediaTicks, long.MinValue);
        Interlocked.Exchange(ref _framesConsumed, 0);
        _inFlightSamples.Clear(); // 与在途缓冲同步清空（Flush/重建路径亦然）
        _clockRunning = false;
        Volatile.Write(ref _lastStepQpc, 0);
        _lastBufferSamples = 0;
        _pacePrevStepQpc = 0; // 重置后首个间隔无前值，跳过（防 Flush/重播间隙被误计欠载）
    }

    /// <inheritdoc/>
    public TimeSpan Latency
    {
        get
        {
            if (!_initialized || _sampleRate <= 0 || _lastBufferSamples <= 0) return TimeSpan.Zero;
            // 估算：在途缓冲深度 × 单缓冲时长（最近完成缓冲的采样数）。
            // 旧实现 MaxInFlightBuffers/_sampleRate 单位错误（缓冲"个数"除以"采样/秒"，差缓冲时长倍数）。
            return TimeSpan.FromSeconds((double)(MaxInFlightBuffers * _lastBufferSamples) / _sampleRate);
        }
    }

    /// <inheritdoc/>
    public float Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _volume = Math.Clamp(value, 0.0f, 1.0f);
            ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        if (_volumeItf == IntPtr.Zero) return;
        // 0.0~1.0 -> millibel：0dB(1.0) 到约 -30dB(0.03)；0 视为静音用极小 mB
        int mb = _volume <= 0.001f ? -6000 : (int)(Math.Log10(_volume) * 2000);
        mb = Math.Clamp(mb, -6000, 0);
        var setVol = GetVTable<SLResult_ShortDelegate>(_volumeItf, SLOT_Volume_SetVolumeLevel);
        setVol(_volumeItf, (short)mb); // SLmillibel = SLint16
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseNdkObjects();

        // 释放缓冲池
        while (_freeBuffers.TryDequeue(out IntPtr b)) Marshal.FreeHGlobal(b);
        while (_inFlightBuffers.TryDequeue(out IntPtr b)) Marshal.FreeHGlobal(b);
        if (_thisHandle.IsAllocated) _thisHandle.Free();
        _bqCallback = null;

        _initialized = false;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask; // 契约方法：无 I/O 可 await，非伪异步
    }

    private static void ThrowIfNotAndroid()
    {
        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("OpenSL ES 输出仅支持 Android。");
    }

    // NDK 结构体（Sequential，匹配 NDK OpenSLES.h 布局）

    [StructLayout(LayoutKind.Sequential)]
    private struct SLDataLocatorAndroidSimpleBufferQueue
    {
        public uint LocatorType;
        public uint NumBuffers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SLDataFormatPcm
    {
        public uint FormatType;
        public uint NumChannels;
        public uint SamplesPerSec;   // milliHz
        public uint BitsPerSample;
        public uint ContainerSize;
        public uint ChannelMask;
        public uint Endianness;
        // 注意：NDK 的 SLDataFormat_PCM 仅 7 字段（28 字节，OpenSLES.h:356-364）；
        // representation 属扩展格式 SLAndroidDataFormat_PCM_EX（formatType=SL_ANDROID_DATAFORMAT_PCM_EX=4），
        // 标准 PCM 格式无此字段，加会多出 4 字节污染布局。
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SLDataLocatorOutputMix
    {
        public uint LocatorType;
        public IntPtr OutputMix;       // SLObjectItf = void**
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SLDataSource
    {
        public IntPtr PLocator;
        public IntPtr PFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SLDataSink
    {
        public IntPtr PLocator;
        public IntPtr PFormat;
    }
}
