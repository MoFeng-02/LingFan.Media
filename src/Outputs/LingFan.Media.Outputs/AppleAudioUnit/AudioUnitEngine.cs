using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.AppleAudioUnit;

/// <summary>
/// Apple AudioUnit 共用播放引擎（macOS DefaultOutput / iOS RemoteIO），平台扩展。
/// </summary>
/// <remarks>
/// <para>职责：通过 AudioToolbox AudioUnit v2 C API 播放交错 S16 PCM。
/// <see cref="CoreAudio.CoreAudioOutput"/>（macOS，kAudioUnitSubType_DefaultOutput）与
/// <see cref="AVAudioEngine.AvAudioEngineOutput"/>（iOS，kAudioUnitSubType_RemoteIO）共用本引擎
/// （走 RemoteIO 共用 AudioUnit 路径，不走 Obj-C AVAudioEngine 封装）。</para>
/// <para><b>数据模型</b>：AudioUnit 为拉模式——注册 <c>AURenderCallback</c>，CoreAudio 实时线程回调拉取 PCM；
/// <see cref="Submit"/> 推入托管环形缓冲（500ms 容量），缓冲满时阻塞等待（带 2 秒超时）自带背压；
/// 回调侧数据不足补零（静音，underrun 不抛错）。</para>
/// <para><b>异步策略</b>（与 WASAPI/AAudio/OpenSL ES 范本一致）：
/// 全部成员为同步——AudioUnit C API 均为同步原生调用，无真实 I/O 可 await。
/// InitializeAsync/DisposeAsync 契约语义由外层包装类承担。</para>
/// <para><b>音量</b>：软件增益（S16 样本缩放，写入环形缓冲前应用），与 AAudio 一致，避免依赖平台差异化音量 API。</para>
/// <para><b>播放位置</b>：渲染回调累计已消费数据帧数（<see cref="Interlocked"/>），换算时间。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API 直接 <c>LibraryImport</c> AudioToolbox.framework；
/// 渲染回调用 <c>[UnmanagedCallersOnly]</c> 静态方法 + <c>GCHandle</c> refCon，零反射、零动态代码、零 Obj-C 运行时依赖。</para>
/// <para><b>平台边界</b>：仅 macOS / iOS 有效；平台守卫由外层包装类负责。编译期跨平台可编译。</para>
/// <para><b>分部划分</b>：Ring 分部管数据通路（Submit 背压/渲染回调/软件增益）；Native 分部管结构体布局与 P/Invoke。
/// 本文件为 AudioUnit 生命周期与播放控制。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe partial class AudioUnitEngine : IDisposable
{
    // AudioUnit 常量（AudioToolbox/AUComponent.h，FourCC）
    /// <summary>kAudioUnitSubType_DefaultOutput（'def '，macOS 默认输出设备）。</summary>
    internal const uint SubTypeDefaultOutput = 0x64656620;
    /// <summary>kAudioUnitSubType_RemoteIO（'rioc'，iOS 音频硬件 I/O）。</summary>
    internal const uint SubTypeRemoteIO = 0x72696F63;

    private const uint TypeOutput = 0x61756F75;          // kAudioUnitType_Output 'auou'
    private const uint ManufacturerApple = 0x6170706C;   // kAudioUnitManufacturer_Apple 'appl'
    private const uint FormatLinearPcm = 0x6C70636D;     // kAudioFormatLinearPCM 'lpcm'
    private const uint FlagsSignedIntPacked = 0x4 | 0x8; // kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked
    private const uint PropertyStreamFormat = 8;         // kAudioUnitProperty_StreamFormat
    private const uint PropertySetRenderCallback = 23;   // kAudioUnitProperty_SetRenderCallback
    private const uint ScopeInput = 1;                   // kAudioUnitScope_Input
    private const int NoErr = 0;

    /// <summary>环形缓冲容量（毫秒）。</summary>
    private const int RingBufferMs = 500;
    /// <summary>Submit 背压等待超时（毫秒）：2 秒，与 AAudio WriteTimeoutNanos 对齐。</summary>
    private const int SubmitTimeoutMs = 2000;

    private readonly uint _componentSubType;
    private readonly string _displayName;

    private IntPtr _audioUnit;
    private GCHandle _selfHandle;
    private bool _readyForInit; // 外层 InitializeAsync 已完成
    private bool _initialized;
    private bool _disposed;
    private int _sampleRate;
    private int _channels;
    private int _bytesPerFrame;
    private float _volume = 1.0f;
    private long _framesRendered; // 渲染回调累计消费的数据帧数（Interlocked）

    /// <param name="componentSubType"><see cref="SubTypeDefaultOutput"/>（macOS）或 <see cref="SubTypeRemoteIO"/>（iOS）。</param>
    /// <param name="displayName">错误消息前缀（如 "CoreAudio" / "AVAudioEngine(RemoteIO)"）。</param>
    internal AudioUnitEngine(uint componentSubType, string displayName)
    {
        _componentSubType = componentSubType;
        _displayName = displayName;
    }

    /// <summary>标记外层 InitializeAsync 契约已完成（同步、无 I/O）。</summary>
    internal void MarkReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readyForInit)
            throw new InvalidOperationException($"{_displayName} 输出已初始化，请勿重复调用 InitializeAsync。");
        _readyForInit = true;
    }

    /// <summary>创建 AudioUnit、设置流格式、注册渲染回调并启动播放。同步原生调用。</summary>
    internal void Initialize(int sampleRate, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_readyForInit)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");
        if (_initialized)
            throw new InvalidOperationException($"{_displayName} 输出已初始化，请先 Dispose 再重新初始化。");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        _sampleRate = sampleRate;
        _channels = channels;
        _bytesPerFrame = channels * 2; // S16 交错

        // 环形缓冲：500ms
        int capacity = sampleRate * _bytesPerFrame * RingBufferMs / 1000;
        _ring = new byte[capacity];
        _readPos = _writePos = _count = 0;

        try
        {
            // 1. 查找输出组件
            var desc = new AudioComponentDescription
            {
                ComponentType = TypeOutput,
                ComponentSubType = _componentSubType,
                ComponentManufacturer = ManufacturerApple
            };
            IntPtr component = AudioComponentFindNext(IntPtr.Zero, ref desc);
            if (component == IntPtr.Zero)
                throw new InvalidOperationException($"AudioComponentFindNext 未找到 {_displayName} 输出组件。");

            // 2. 实例化 AudioUnit
            int status = AudioComponentInstanceNew(component, out _audioUnit);
            if (status != NoErr || _audioUnit == IntPtr.Zero)
                throw new InvalidOperationException($"AudioComponentInstanceNew 失败，OSStatus={status}。");

            // 3. 设置输入侧流格式（交错 S16 lpcm）
            var asbd = new AudioStreamBasicDescription
            {
                SampleRate = sampleRate,
                FormatId = FormatLinearPcm,
                FormatFlags = FlagsSignedIntPacked,
                BytesPerPacket = (uint)_bytesPerFrame,
                FramesPerPacket = 1,
                BytesPerFrame = (uint)_bytesPerFrame,
                ChannelsPerFrame = (uint)channels,
                BitsPerChannel = 16
            };
            status = AudioUnitSetPropertyFormat(_audioUnit, PropertyStreamFormat, ScopeInput, 0,
                ref asbd, (uint)sizeof(AudioStreamBasicDescription));
            if (status != NoErr)
                throw new InvalidOperationException($"AudioUnitSetProperty(StreamFormat) 失败，OSStatus={status}。");

            // 4. 注册渲染回调（UnmanagedCallersOnly 静态方法 + GCHandle refCon，AOT 安全）
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            var cb = new AURenderCallbackStruct
            {
                InputProc = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, uint, AudioBufferList*, int>)&RenderCallback,
                InputProcRefCon = GCHandle.ToIntPtr(_selfHandle)
            };
            status = AudioUnitSetPropertyCallback(_audioUnit, PropertySetRenderCallback, ScopeInput, 0,
                ref cb, (uint)sizeof(AURenderCallbackStruct));
            if (status != NoErr)
                throw new InvalidOperationException($"AudioUnitSetProperty(SetRenderCallback) 失败，OSStatus={status}。");

            // 5. 初始化并启动
            status = AudioUnitInitialize(_audioUnit);
            if (status != NoErr)
                throw new InvalidOperationException($"AudioUnitInitialize 失败，OSStatus={status}。");

            status = AudioOutputUnitStart(_audioUnit);
            if (status != NoErr)
                throw new InvalidOperationException($"AudioOutputUnitStart 失败，OSStatus={status}。");

            _initialized = true;
        }
        catch
        {
            ReleaseAudioUnit();
            throw;
        }
    }

    /// <summary>暂停播放（AudioOutputUnitStop，同步）。</summary>
    internal void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioUnit == IntPtr.Zero) return;
        _ = AudioOutputUnitStop(_audioUnit);
    }

    /// <summary>恢复播放（AudioOutputUnitStart，同步）。</summary>
    internal void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioUnit == IntPtr.Zero) return;
        _ = AudioOutputUnitStart(_audioUnit);
    }

    /// <summary>清空环形缓冲中未播放的数据（同步、纯内存）。</summary>
    internal void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) return;
        lock (_gate)
        {
            _readPos = _writePos = _count = 0;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>播放位置：渲染回调已消费的数据帧数换算时间（同步、纯内存）。</summary>
    internal TimeSpan GetPlaybackPosition()
    {
        if (!_initialized || _sampleRate <= 0) return TimeSpan.Zero;
        long frames = Interlocked.Read(ref _framesRendered);
        return frames <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)frames / _sampleRate);
    }

    /// <summary>延迟估算：环形缓冲容量时长（同步、纯内存）。</summary>
    internal TimeSpan Latency
    {
        get
        {
            if (!_initialized || _sampleRate <= 0 || _bytesPerFrame <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)(_ring.Length / _bytesPerFrame) / _sampleRate);
        }
    }

    /// <summary>音量（0.0~1.0，软件增益，Submit 时应用）。</summary>
    internal float Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _volume = Math.Clamp(value, 0.0f, 1.0f);
        }
    }

    /// <summary>同步快速释放：停止播放、反初始化并销毁 AudioUnit。</summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate); // 唤醒阻塞中的 Submit（其检测 _disposed 后抛 ObjectDisposedException）
        }

        ReleaseAudioUnit();

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _initialized = false;
        _readyForInit = false;
    }

    private void ReleaseAudioUnit()
    {
        if (_audioUnit == IntPtr.Zero) return;
        try
        {
            _ = AudioOutputUnitStop(_audioUnit);
            _ = AudioUnitUninitialize(_audioUnit);
            _ = AudioComponentInstanceDispose(_audioUnit);
        }
        catch { /* 忽略释放错误 */ }
        _audioUnit = IntPtr.Zero;
    }
}