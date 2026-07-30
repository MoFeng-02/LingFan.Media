using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.AppleAudioUnit;

/// <summary>
/// Apple AudioUnit 共用播放引擎（macOS DefaultOutput / iOS RemoteIO）。P2 平台扩展（V2-18 / O2+O3）。
/// </summary>
/// <remarks>
/// <para>职责：通过 AudioToolbox AudioUnit v2 C API 播放交错 S16 PCM。
/// <see cref="CoreAudio.CoreAudioOutput"/>（macOS，kAudioUnitSubType_DefaultOutput）与
/// <see cref="AVAudioEngine.AvAudioEngineOutput"/>（iOS，kAudioUnitSubType_RemoteIO）共用本引擎
/// （用户 2026-07-28 拍板：O3 走 RemoteIO 共用 AudioUnit 路径，不走 Obj-C AVAudioEngine 封装）。</para>
/// <para><b>数据模型</b>：AudioUnit 为拉模式——注册 <c>AURenderCallback</c>，CoreAudio 实时线程回调拉取 PCM；
/// <see cref="Submit"/> 推入托管环形缓冲（500ms 容量），缓冲满时阻塞等待（带 2 秒超时）自带背压；
/// 回调侧数据不足补零（静音，underrun 不抛错）。</para>
/// <para><b>异步策略</b>（与 WASAPI/AAudio/OpenSL ES 范本一致，遵守总记忆第十二章）：
/// 全部成员为同步（native/sync 分类）——AudioUnit C API 均为同步原生调用，无真实 I/O 可 await。
/// InitializeAsync/DisposeAsync 契约语义由外层包装类承担。</para>
/// <para><b>音量</b>：软件增益（S16 样本缩放，写入环形缓冲前应用），与 AAudio 一致，避免依赖平台差异化音量 API。</para>
/// <para><b>播放位置</b>：渲染回调累计已消费数据帧数（<see cref="Interlocked"/>），换算时间。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API 直接 <c>LibraryImport</c> AudioToolbox.framework；
/// 渲染回调用 <c>[UnmanagedCallersOnly]</c> 静态方法 + <c>GCHandle</c> refCon，零反射、零动态代码、零 Obj-C 运行时依赖。</para>
/// <para><b>平台边界</b>：仅 macOS / iOS 有效；平台守卫由外层包装类负责。编译期跨平台可编译。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe partial class AudioUnitEngine : IDisposable
{
    private const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // ── AudioUnit 常量（AudioToolbox/AUComponent.h，FourCC）──
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

    // 环形缓冲（托管，回调与 Submit 经 _gate 同步）
    private readonly object _gate = new();
    private byte[] _ring = [];
    private int _readPos;
    private int _writePos;
    private int _count;

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

    /// <summary>创建 AudioUnit、设置流格式、注册渲染回调并启动播放。同步原生调用（sync 分类）。</summary>
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

    /// <summary>提交音频帧：软件增益后写入环形缓冲（满时阻塞背压，2 秒超时）。同步边界（native 分类）；不接管帧所有权（V2 规则）。</summary>
    internal void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _audioUnit == IntPtr.Zero)
            throw new InvalidOperationException($"{_displayName} 输出尚未初始化，无法提交音频帧。");
        if (frame.Channels != _channels)
            throw new ArgumentException($"音频帧声道数 {frame.Channels} 与输出配置 {_channels} 不匹配。", nameof(frame));

        // 仅支持 S16（流格式固定 lpcm S16；上游需保证一致，否则由宿主/管线转换）
        if (frame.SampleFormat != SampleFormat.S16)
            throw new NotSupportedException($"{_displayName} 输出仅支持 S16，收到 {frame.SampleFormat}。");

        int byteLength = frame.FrameCount * frame.Channels * 2; // S16
        if (frame.Data.Length < byteLength)
            throw new ArgumentException($"音频帧数据不足：期望 {byteLength} 字节，实际 {frame.Data.Length} 字节。", nameof(frame));

        ReadOnlySpan<byte> src = frame.Data.Span[..byteLength];

        if (_volume >= 0.999f)
        {
            WriteToRing(src);
            return;
        }

        // 软件增益：S16 样本缩放到租用缓冲（与 AAudio 一致）
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            Span<byte> dst = rented.AsSpan(0, byteLength);
            src.CopyTo(dst);
            ApplyGainS16(dst, _volume);
            WriteToRing(dst);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void WriteToRing(ReadOnlySpan<byte> src)
    {
        int offset = 0;
        lock (_gate)
        {
            while (offset < src.Length)
            {
                while (_count == _ring.Length)
                {
                    if (!Monitor.Wait(_gate, SubmitTimeoutMs))
                        throw new TimeoutException($"{_displayName} 输出缓冲写入超时（2 秒内渲染回调未释放空间）。");
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }

                int toWrite = Math.Min(_ring.Length - _count, src.Length - offset);
                int firstSegment = Math.Min(toWrite, _ring.Length - _writePos);
                src.Slice(offset, firstSegment).CopyTo(_ring.AsSpan(_writePos));
                if (toWrite > firstSegment)
                    src.Slice(offset + firstSegment, toWrite - firstSegment).CopyTo(_ring.AsSpan(0));

                _writePos = (_writePos + toWrite) % _ring.Length;
                _count += toWrite;
                offset += toWrite;
            }
        }
    }

    /// <summary>渲染回调（CoreAudio 实时线程）：从环形缓冲拉取 PCM，不足补零静音。</summary>
    [UnmanagedCallersOnly]
    private static int RenderCallback(IntPtr refCon, IntPtr ioActionFlags, IntPtr inTimeStamp,
        uint inBusNumber, uint inNumberFrames, AudioBufferList* ioData)
    {
        if (ioData == null)
            return NoErr;

        var engine = GCHandle.FromIntPtr(refCon).Target as AudioUnitEngine;
        AudioBuffer* buffers = &ioData->Buffer0;

        for (uint i = 0; i < ioData->NumberBuffers; i++)
        {
            AudioBuffer* buf = buffers + i;
            if (buf->Data == IntPtr.Zero || buf->DataByteSize == 0)
                continue;

            var dst = new Span<byte>((void*)buf->Data, (int)buf->DataByteSize);
            int copied = 0;

            if (engine is not null && !engine._disposed)
            {
                lock (engine._gate)
                {
                    int toCopy = Math.Min(dst.Length, engine._count);
                    int firstSegment = Math.Min(toCopy, engine._ring.Length - engine._readPos);
                    engine._ring.AsSpan(engine._readPos, firstSegment).CopyTo(dst);
                    if (toCopy > firstSegment)
                        engine._ring.AsSpan(0, toCopy - firstSegment).CopyTo(dst[firstSegment..]);

                    engine._readPos = (engine._readPos + toCopy) % Math.Max(engine._ring.Length, 1);
                    engine._count -= toCopy;
                    copied = toCopy;
                    Monitor.PulseAll(engine._gate);
                }

                if (engine._bytesPerFrame > 0)
                    Interlocked.Add(ref engine._framesRendered, copied / engine._bytesPerFrame);
            }

            if (copied < dst.Length)
                dst[copied..].Clear(); // underrun → 静音
        }

        return NoErr;
    }

    /// <summary>对 S16 交错 PCM 应用线性增益（就地缩放）。</summary>
    private static void ApplyGainS16(Span<byte> pcm, float gain)
    {
        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
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

    /// <summary>同步快速释放：停止播放、反初始化并销毁 AudioUnit（sync 分类）。</summary>
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

    // ── AudioToolbox 结构体（Sequential，匹配 CoreAudioTypes.h / AUComponent.h 布局）──

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioComponentDescription
    {
        public uint ComponentType;
        public uint ComponentSubType;
        public uint ComponentManufacturer;
        public uint ComponentFlags;
        public uint ComponentFlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AURenderCallbackStruct
    {
        public IntPtr InputProc;
        public IntPtr InputProcRefCon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer0; // 变长数组首元素（后续元素经指针步进访问）
    }

    // ── AudioToolbox P/Invoke（AudioComponent + AudioUnit v2 API，macOS/iOS 同路径）──

    [LibraryImport(AudioToolboxLibrary)]
    private static partial IntPtr AudioComponentFindNext(IntPtr inComponent, ref AudioComponentDescription inDesc);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioComponentInstanceNew(IntPtr inComponent, out IntPtr outInstance);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioComponentInstanceDispose(IntPtr inInstance);

    [LibraryImport(AudioToolboxLibrary, EntryPoint = "AudioUnitSetProperty")]
    private static partial int AudioUnitSetPropertyFormat(IntPtr inUnit, uint inId, uint inScope, uint inElement,
        ref AudioStreamBasicDescription inData, uint inDataSize);

    [LibraryImport(AudioToolboxLibrary, EntryPoint = "AudioUnitSetProperty")]
    private static partial int AudioUnitSetPropertyCallback(IntPtr inUnit, uint inId, uint inScope, uint inElement,
        ref AURenderCallbackStruct inData, uint inDataSize);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioUnitInitialize(IntPtr inUnit);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioUnitUninitialize(IntPtr inUnit);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioOutputUnitStart(IntPtr inUnit);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioOutputUnitStop(IntPtr inUnit);
}
