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
/// <item><see cref="Initialize"/>：同步（sync 分类），构建 PCM 格式/数据源/数据汇并创建播放器。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），拷贝 PCM 到原生缓冲并 <c>Enqueue</c>；缓冲满时由缓冲队列计数做背压（阻塞等待）。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），SetPlayState/BufferQueue.Clear。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，OpenSL ES 无直接时钟，返回 <see cref="TimeSpan.Zero"/>（占位，待 Surface/宿主时钟桥接）。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），Destroy 所有 NDK 对象。</item>
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

    // ── OpenSL ES 常量（NDK OpenSLES.h）──
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

    private const int MaxInFlightBuffers = 4;

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

    // ── libOpenSLES P/Invoke ──
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

    private void InitializeCore()
    {
        // 1. 创建引擎对象
        int ret = slCreateEngine(out _engineObject, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        if (ret != SL_RESULT_SUCCESS || _engineObject == IntPtr.Zero)
            throw new InvalidOperationException($"slCreateEngine 失败，code={ret}。");

        try
        {
            // 2. Realize 引擎
            var realize = GetVTable<SLResult_BoolDelegate>(_engineObject, SLOT_Object_Realize);
            ret = realize(_engineObject, SL_BOOLEAN_FALSE);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"引擎 Realize 失败，code={ret}。");

            // 3. 获取 SLEngineItf
            var getIface = GetVTable<SLResult_IntPtrDelegate>(_engineObject, SLOT_Object_GetInterface);
            ret = getIface(_engineObject, GetIid("SL_IID_ENGINE"), out _engineItf);
            if (ret != SL_RESULT_SUCCESS || _engineItf == IntPtr.Zero)
                throw new InvalidOperationException($"获取 SLEngineItf 失败，code={ret}。");

            // 4. 创建 OutputMix
            var createMix = GetVTable<SLResult_OutObjDelegate>(_engineItf, SLOT_Engine_CreateOutputMix);
            ret = createMix(_engineItf, out _outputMixObject, 0, IntPtr.Zero, IntPtr.Zero);
            if (ret != SL_RESULT_SUCCESS || _outputMixObject == IntPtr.Zero)
                throw new InvalidOperationException($"CreateOutputMix 失败，code={ret}。");

            var realizeMix = GetVTable<SLResult_BoolDelegate>(_outputMixObject, SLOT_Object_Realize);
            ret = realizeMix(_outputMixObject, SL_BOOLEAN_FALSE);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"OutputMix Realize 失败，code={ret}。");
        }
        catch
        {
            ReleaseNdkObjects();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engineItf == IntPtr.Zero)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");
        if (_initialized)
            throw new InvalidOperationException("OpenSL ES 输出已初始化，请先 Dispose 再重新初始化。");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        _sampleRate = sampleRate;
        _channels = channels;

        _logger.LogInformation("[OPENSLES] 播放器创建: {Rate}Hz/{Ch}ch S16", sampleRate, channels);
        CreatePlayer(sampleRate, channels);
        _initialized = true;
    }

    /// <summary>
    /// 创建 OpenSL ES 音频播放器，绑定采样率/声道数（engine / outputmix 复用）。
    /// 帧格式变化时经 <see cref="RecreatePlayer"/> 销毁并按新格式重建。
    /// </summary>
    private void CreatePlayer(int sampleRate, int channels)
    {
        uint slSampleRate = sampleRate switch
        {
            44100 => SL_SAMPLINGRATE_44_1,
            48000 => SL_SAMPLINGRATE_48,
            _ => (uint)(sampleRate * 1000) // milliHz
        };

        var bqLocator = new SLDataLocatorAndroidSimpleBufferQueue
        {
            LocatorType = SL_DATALOCATOR_ANDROIDSIMPLEBUFFERQUEUE,
            NumBuffers = MaxInFlightBuffers
        };
        var pcm = new SLDataFormatPcm
        {
            FormatType = SL_DATAFORMAT_PCM,
            NumChannels = (uint)channels,
            SamplesPerSec = slSampleRate,
            BitsPerSample = SL_PCMSAMPLEFORMAT_FIXED_16,
            ContainerSize = SL_PCMSAMPLEFORMAT_FIXED_16,
            ChannelMask = (channels >= 2) ? (SL_SPEAKER_FRONT_LEFT | SL_SPEAKER_FRONT_RIGHT) : SL_SPEAKER_FRONT_LEFT,
            Endianness = SL_BYTEORDER_LITTLEENDIAN
        };

        GCHandle bqHandle = default, pcmHandle = default, outMixHandle = default, srcHandle = default, snkHandle = default, idsHandle = default, reqHandle = default;
        try
        {
            bqHandle = GCHandle.Alloc(bqLocator, GCHandleType.Pinned);
            pcmHandle = GCHandle.Alloc(pcm, GCHandleType.Pinned);

            var dataSource = new SLDataSource
            {
                PLocator = bqHandle.AddrOfPinnedObject(),
                PFormat = pcmHandle.AddrOfPinnedObject()
            };
            srcHandle = GCHandle.Alloc(dataSource, GCHandleType.Pinned);

            var outMixLocator = new SLDataLocatorOutputMix
            {
                LocatorType = SL_DATALOCATOR_OUTPUTMIX,
                OutputMix = _outputMixObject
            };
            outMixHandle = GCHandle.Alloc(outMixLocator, GCHandleType.Pinned);

            var dataSink = new SLDataSink
            {
                PLocator = outMixHandle.AddrOfPinnedObject(),
                PFormat = IntPtr.Zero
            };
            snkHandle = GCHandle.Alloc(dataSink, GCHandleType.Pinned);

            // 接口请求：BUFFERQUEUE + VOLUME
            IntPtr[] ids = { GetIid("SL_IID_BUFFERQUEUE"), GetIid("SL_IID_VOLUME") };
            uint[] req = { SL_BOOLEAN_TRUE, SL_BOOLEAN_TRUE }; // SLboolean[] = SLuint32[]（4 字节/元素）
            idsHandle = GCHandle.Alloc(ids, GCHandleType.Pinned);
            reqHandle = GCHandle.Alloc(req, GCHandleType.Pinned);

            var createPlayer = GetVTable<SLResult_SourceSinkDelegate>(_engineItf, SLOT_Engine_CreateAudioPlayer);
            int ret = createPlayer(_engineItf, out _playerObject, srcHandle.AddrOfPinnedObject(),
                snkHandle.AddrOfPinnedObject(), (uint)ids.Length, idsHandle.AddrOfPinnedObject(), reqHandle.AddrOfPinnedObject());
            if (ret != SL_RESULT_SUCCESS || _playerObject == IntPtr.Zero)
                throw new InvalidOperationException($"CreateAudioPlayer 失败，code={ret}。");

            var realizePlayer = GetVTable<SLResult_BoolDelegate>(_playerObject, SLOT_Object_Realize);
            ret = realizePlayer(_playerObject, SL_BOOLEAN_FALSE);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"播放器 Realize 失败，code={ret}。");

            // 获取接口：PLAY / BUFFERQUEUE / VOLUME
            var getIface = GetVTable<SLResult_IntPtrDelegate>(_playerObject, SLOT_Object_GetInterface);
            ret = getIface(_playerObject, GetIid("SL_IID_PLAY"), out _playItf);
            if (ret != SL_RESULT_SUCCESS || _playItf == IntPtr.Zero)
                throw new InvalidOperationException($"获取 SLPlayItf 失败，code={ret}。");

            ret = getIface(_playerObject, GetIid("SL_IID_BUFFERQUEUE"), out _bqItf);
            if (ret != SL_RESULT_SUCCESS || _bqItf == IntPtr.Zero)
                throw new InvalidOperationException($"获取 SLAndroidSimpleBufferQueueItf 失败，code={ret}。");

            // VOLUME 可选
            if (getIface(_playerObject, GetIid("SL_IID_VOLUME"), out _volumeItf) != SL_RESULT_SUCCESS)
                _volumeItf = IntPtr.Zero;

            // 注册缓冲队列回调（完成时回收缓冲 + 释放背压信号）
            _thisHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            _bqCallback = OnBufferCompleted;
            var registerCb = GetVTable<SLResult_RegisterCallbackDelegate>(_bqItf, SLOT_BQ_RegisterCallback);
            registerCb(_bqItf, _bqCallback, GCHandle.ToIntPtr(_thisHandle));

            // 缓冲池起始不预分配：Submit 按需从池取/分配，回调回收。

            // 开始播放
            var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
            setState(_playItf, SL_PLAYSTATE_PLAYING);

            ApplyVolume();
        }
        finally
        {
            if (bqHandle.IsAllocated) bqHandle.Free();
            if (pcmHandle.IsAllocated) pcmHandle.Free();
            if (outMixHandle.IsAllocated) outMixHandle.Free();
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (snkHandle.IsAllocated) snkHandle.Free();
            if (idsHandle.IsAllocated) idsHandle.Free();
            if (reqHandle.IsAllocated) reqHandle.Free();
        }
    }

    /// <summary>
    /// 按新格式重建播放器：Submit 发现帧采样率/声道数与当前配置不一致时调用。
    /// 已知事实（HE-AAC v2：码流参数 22050Hz/1ch，SBR+PS 解码后实际输出 44100Hz/2ch）——
    /// MediaCodec 的 configure 初始输出格式不可信，真实格式仅首帧 FORMAT_CHANGED 后可知，
    /// 故解码器/MediaPlayer 侧无从预知，正确做法是输出端运行时自适应。
    /// 仅销毁并重建绑定格式的 player（engine / outputmix 保持）；重建前清空缓冲池
    /// （旧格式缓冲大小不匹配新格式，复用会越界写），并重置背压信号。
    /// </summary>
    private void RecreatePlayer(int sampleRate, int channels)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        lock (_gate)
        {
            // 1. 停止旧播放器（不再消费缓冲 → 不再触发完成回调）
            if (_playItf != IntPtr.Zero)
            {
                try
                {
                    var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
                    setState(_playItf, SL_PLAYSTATE_STOPPED);
                }
                catch { /* 忽略停止错误 */ }
            }

            // 2. 清空缓冲池（旧格式缓冲大小不匹配，复用会越界）并重置背压信号
            while (_inFlightBuffers.TryDequeue(out IntPtr b)) Marshal.FreeHGlobal(b);
            while (_freeBuffers.TryDequeue(out IntPtr b)) Marshal.FreeHGlobal(b);
            int deficit = MaxInFlightBuffers - _backpressure.CurrentCount;
            if (deficit > 0) _backpressure.Release(deficit);

            // 3. 销毁旧 player（play/bq/volume 接口随之失效）并释放回调句柄
            _playItf = IntPtr.Zero;
            _bqItf = IntPtr.Zero;
            _volumeItf = IntPtr.Zero;
            DestroyObject(ref _playerObject);
            if (_thisHandle.IsAllocated) _thisHandle.Free();
        }

        int oldRate = _sampleRate;
        int oldChannels = _channels;
        _sampleRate = sampleRate;
        _channels = channels;

        // 4. 按新格式重建（_gate 外执行，避免长锁；Submit 单线程调用，串行安全）
        _logger.LogWarning("[OPENSLES] 帧格式与播放器不符，重建播放器: {OldRate}Hz/{OldCh}ch → {NewRate}Hz/{NewCh}ch",
            oldRate, oldChannels, sampleRate, channels);
        CreatePlayer(sampleRate, channels);
    }

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _bqItf == IntPtr.Zero)
            throw new InvalidOperationException("OpenSL ES 输出尚未初始化，无法 Submit。");

        // 运行时格式重协商：帧采样率/声道数与当前播放器配置不一致时重建播放器。
        // MediaCodec 真实输出格式仅首帧 FORMAT_CHANGED 后可知（configure 初始值不可信），
        // 首帧到达即触发重建（HE-AAC v2 等场景 1ch→2ch、采样率翻倍）。
        if (frame.Channels != _channels || frame.SampleRate != _sampleRate)
        {
            if (frame.SampleRate <= 0 || frame.Channels <= 0)
                throw new ArgumentException($"音频帧采样率/声道数非法：{frame.SampleRate}Hz/{frame.Channels}ch。", nameof(frame));
            RecreatePlayer(frame.SampleRate, frame.Channels);
        }

        // 仅支持 S16（OpenSL ES 输出固定 S16；上游需保证一致，否则由宿主/管线转换）
        if (frame.SampleFormat != SampleFormat.S16)
            throw new NotSupportedException($"OpenSL ES 输出仅支持 S16，收到 {frame.SampleFormat}。");

        int byteLength = frame.FrameCount * frame.Channels * 2; // S16
        if (frame.Data.Length < byteLength)
            throw new ArgumentException($"音频帧数据不足：期望 {byteLength} 字节，实际 {frame.Data.Length} 字节。", nameof(frame));

        // 背压：等待有空闲缓冲（最多 MaxInFlightBuffers 个在途）
        _backpressure.Wait();

        // 取一个空闲原生缓冲（无则分配）
        if (!_freeBuffers.TryDequeue(out IntPtr buffer))
            buffer = Marshal.AllocHGlobal(byteLength);

        try
        {
            // 拷贝 PCM（S16）到原生缓冲
            var src = frame.Data.Span[..byteLength];
            var dst = new Span<byte>((void*)buffer, byteLength);
            src.CopyTo(dst);

            _inFlightBuffers.Enqueue(buffer);

            var enqueue = GetVTable<SLResult_EnqueueDelegate>(_bqItf, SLOT_BQ_Enqueue);
            int ret = enqueue(_bqItf, buffer, (uint)byteLength);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"BufferQueue Enqueue 失败，code={ret}。");
        }
        catch
        {
            // Enqueue 失败：回收缓冲 + 释放背压
            if (buffer != IntPtr.Zero)
            {
                _freeBuffers.Enqueue(buffer);
                _backpressure.Release();
            }
            throw;
        }
    }

    private void OnBufferCompleted(IntPtr bq, IntPtr context)
    {
        // 一个缓冲播放完成：回收一个在途缓冲到空闲池，并释放背压信号。
        // 锁 _gate：重建窗口（STOPPED→清池→Destroy）内可能收到迟到回调；
        // TryDequeue 失败（池已被清空）则不 Release，防止信号量计数溢出（SemaphoreFullException）。
        lock (_gate)
        {
            if (_inFlightBuffers.TryDequeue(out IntPtr buffer))
            {
                _freeBuffers.Enqueue(buffer);
                _backpressure.Release();
            }
        }
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _playItf == IntPtr.Zero) return;
        var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
        setState(_playItf, SL_PLAYSTATE_STOPPED);
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _playItf == IntPtr.Zero) return;
        var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
        setState(_playItf, SL_PLAYSTATE_PLAYING);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _bqItf == IntPtr.Zero) return;
        var clear = GetVTable<SLResult_VoidDelegate>(_bqItf, SLOT_BQ_Clear);
        clear(_bqItf);
        // 清空在途缓冲回到空闲池
        while (_inFlightBuffers.TryDequeue(out IntPtr buf))
            _freeBuffers.Enqueue(buf);
        _backpressure.Release(MaxInFlightBuffers - _backpressure.CurrentCount);
    }

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => TimeSpan.Zero; // OpenSL ES 无直接时钟；占位

    /// <inheritdoc/>
    public TimeSpan Latency
    {
        get
        {
            if (!_initialized || _sampleRate <= 0) return TimeSpan.Zero;
            // 估算：MaxInFlightBuffers 个缓冲、每缓冲一帧的保守延迟
            return TimeSpan.FromSeconds((double)MaxInFlightBuffers / _sampleRate);
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

    private static T GetVTable<T>(IntPtr obj, int slot) where T : Delegate
    {
        // OpenSL ES 对象为 void**：*obj 是方法表指针
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static IntPtr GetIid(string name)
    {
        if (NativeLibrary.TryGetExport(NativeLibrary.Load("libOpenSLES.so"), name, out IntPtr addr))
            return Marshal.ReadIntPtr(addr);
        throw new PlatformNotSupportedException($"无法解析 OpenSL ES 接口 ID：{name}。");
    }

    private void ReleaseNdkObjects()
    {
        if (_playItf != IntPtr.Zero)
        {
            try
            {
                // 先停止播放（SetPlayState STOPPED），再销毁对象
                var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
                setState(_playItf, SL_PLAYSTATE_STOPPED);
            }
            catch { /* 忽略停止错误 */ }
            _playItf = IntPtr.Zero;
        }
        _bqItf = IntPtr.Zero;
        _volumeItf = IntPtr.Zero;

        DestroyObject(ref _playerObject);
        DestroyObject(ref _outputMixObject);
        DestroyObject(ref _engineObject);
        _engineItf = IntPtr.Zero;
    }

    private static void DestroyObject(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        try
        {
            var destroy = GetVTable<Void_SelfDelegate>(obj, SLOT_Object_Destroy); // void Destroy(self)
            destroy(obj);
        }
        catch { /* 忽略释放错误 */ }
        obj = IntPtr.Zero;
    }

    // ── NDK 结构体（Sequential，匹配 NDK OpenSLES.h 布局）──

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

    private static void ThrowIfNotAndroid()
    {
        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("OpenSL ES 输出仅支持 Android。");
    }
}
