using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.AAudio;

/// <summary>
/// AAudio 音频输出（Android API 27+）。P2 平台扩展（V2-17 / O5）。
/// </summary>
/// <remarks>
/// <para>职责：通过 Android NDK AAudio API 播放 PCM 数据（libaaudio.so）。
/// Android 8.1（API 27）起稳定；低版本由 DI 侧回退到 OpenSL ES（见 AAudioExtensions）。</para>
/// <para><b>异步策略</b>（与 WASAPI/OpenSL ES 范本一致，遵守总记忆第十二章）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，平台校验后返回 <see cref="Task.CompletedTask"/>。
/// 无 I/O 可 await，<b>非伪异步</b>（不加 <c>async</c> 关键字、方法体无 <c>await</c>）。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），创建 StreamBuilder、打开流并启动。全部为 NDK 同步原生调用。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），<c>AAudioStream_write</c> 阻塞式写入自带背压（带超时）。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），requestPause/requestStart/requestFlush。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，<c>AAudioStream_getFramesRead</c>（设备已消费帧数）换算时间。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），requestStop + close。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。</item>
/// </list>
/// <para><b>音量</b>：AAudio 无原生音量 API，采用软件增益（S16 样本缩放，写入前应用）。</para>
/// <para><b>所有权</b>：Submit 不接管帧所有权、不 Dispose 帧（V2 规则），调用方负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API 直接 <c>LibraryImport</c>，零 COM、零反射、零动态代码。</para>
/// <para><b>平台边界</b>：仅 Android（API 27+）有效；非 Android 调用抛 <see cref="PlatformNotSupportedException"/>。编译期跨平台可编译。</para>
/// </remarks>
[SupportedOSPlatform("Android")]
internal sealed unsafe partial class AAudioOutput : IAudioOutput
{
    // ── AAudio 常量（NDK AAudio.h）──
    private const int AAUDIO_OK = 0;
    private const int AAUDIO_FORMAT_PCM_I16 = 1;
    private const int AAUDIO_DIRECTION_OUTPUT = 0;
    private const int AAUDIO_PERFORMANCE_MODE_LOW_LATENCY = 12;

    /// <summary>阻塞写入超时（纳秒）：2 秒。超时返回已写入帧数（可能少于请求）。</summary>
    private const long WriteTimeoutNanos = 2_000_000_000L;

    private IntPtr _stream;
    private bool _readyForInit; // InitializeAsync 已完成
    private bool _initialized;
    private bool _disposed;
    private int _sampleRate;
    private int _channels;
    private float _volume = 1.0f;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        ThrowIfNotAndroid();
        if (_readyForInit)
            throw new InvalidOperationException("AAudio 输出已初始化，请勿重复调用 InitializeAsync。");
        _readyForInit = true;
        return Task.CompletedTask; // 契约方法：无真实 I/O await，非伪异步
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_readyForInit)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");
        if (_initialized)
            throw new InvalidOperationException("AAudio 输出已初始化，请先 Dispose 再重新初始化。");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        _sampleRate = sampleRate;
        _channels = channels;

        IntPtr builder = IntPtr.Zero;
        try
        {
            int ret = AAudio_createStreamBuilder(out builder);
            if (ret != AAUDIO_OK || builder == IntPtr.Zero)
                throw new InvalidOperationException($"AAudio_createStreamBuilder 失败，code={ret}。");

            AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
            AAudioStreamBuilder_setSampleRate(builder, sampleRate);
            AAudioStreamBuilder_setChannelCount(builder, channels);
            AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
            AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);

            ret = AAudioStreamBuilder_openStream(builder, out _stream);
            if (ret != AAUDIO_OK || _stream == IntPtr.Zero)
                throw new InvalidOperationException($"AAudioStreamBuilder_openStream 失败，code={ret}。");

            ret = AAudioStream_requestStart(_stream);
            if (ret != AAUDIO_OK)
                throw new InvalidOperationException($"AAudioStream_requestStart 失败，code={ret}。");

            _initialized = true;
        }
        catch
        {
            if (_stream != IntPtr.Zero) { _ = AAudioStream_close(_stream); _stream = IntPtr.Zero; }
            throw;
        }
        finally
        {
            if (builder != IntPtr.Zero) _ = AAudioStreamBuilder_delete(builder);
        }
    }

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _stream == IntPtr.Zero)
            throw new InvalidOperationException("AAudio 输出尚未初始化，无法提交音频帧。");
        if (frame.Channels != _channels)
            throw new ArgumentException($"音频帧声道数 {frame.Channels} 与输出配置 {_channels} 不匹配。", nameof(frame));

        // 仅支持 S16（AAudio 流固定 PCM_I16；上游需保证一致，否则由宿主/管线转换）
        if (frame.SampleFormat != SampleFormat.S16)
            throw new NotSupportedException($"AAudio 输出仅支持 S16，收到 {frame.SampleFormat}。");

        int byteLength = frame.FrameCount * frame.Channels * 2; // S16
        if (frame.Data.Length < byteLength)
            throw new ArgumentException($"音频帧数据不足：期望 {byteLength} 字节，实际 {frame.Data.Length} 字节。", nameof(frame));

        ReadOnlySpan<byte> src = frame.Data.Span[..byteLength];

        if (_volume >= 0.999f)
        {
            WriteAll(src, frame.FrameCount);
            return;
        }

        // 软件增益：S16 样本缩放到租用缓冲（AAudio 无原生音量 API）
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            Span<byte> dst = rented.AsSpan(0, byteLength);
            src.CopyTo(dst);
            ApplyGainS16(dst, _volume);
            WriteAll(dst, frame.FrameCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void WriteAll(ReadOnlySpan<byte> pcm, int totalFrames)
    {
        int bytesPerFrame = _channels * 2; // S16
        int framesWritten = 0;
        fixed (byte* p = pcm)
        {
            while (framesWritten < totalFrames)
            {
                int ret = AAudioStream_write(
                    _stream,
                    (IntPtr)(p + (long)framesWritten * bytesPerFrame),
                    totalFrames - framesWritten,
                    WriteTimeoutNanos);
                if (ret < 0)
                    throw new InvalidOperationException($"AAudioStream_write 失败，code={ret}。");
                if (ret == 0)
                    throw new TimeoutException("AAudioStream_write 超时（2 秒内未写入任何帧）。");
                framesWritten += ret;
            }
        }
    }

    /// <summary>对 S16 交错 PCM 应用线性增益（就地缩放）。</summary>
    private static void ApplyGainS16(Span<byte> pcm, float gain)
    {
        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _stream == IntPtr.Zero) return;
        _ = AAudioStream_requestPause(_stream);
    }

    /// <inheritdoc/>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _stream == IntPtr.Zero) return;
        _ = AAudioStream_requestStart(_stream);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _stream == IntPtr.Zero) return;
        // AAudio 约束：requestFlush 仅在 PAUSED 状态有效 → 先暂停再冲刷；恢复播放由 Resume() 负责
        _ = AAudioStream_requestPause(_stream);
        _ = AAudioStream_requestFlush(_stream);
    }

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition()
    {
        if (!_initialized || _stream == IntPtr.Zero || _sampleRate <= 0) return TimeSpan.Zero;
        long framesRead = AAudioStream_getFramesRead(_stream); // 设备已消费的帧数
        return framesRead <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)framesRead / _sampleRate);
    }

    /// <inheritdoc/>
    public TimeSpan Latency
    {
        get
        {
            if (!_initialized || _stream == IntPtr.Zero || _sampleRate <= 0) return TimeSpan.Zero;
            int bufferFrames = AAudioStream_getBufferSizeInFrames(_stream);
            return bufferFrames <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)bufferFrames / _sampleRate);
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
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_stream != IntPtr.Zero)
        {
            try
            {
                _ = AAudioStream_requestStop(_stream);
                _ = AAudioStream_close(_stream);
            }
            catch { /* 忽略释放错误 */ }
            _stream = IntPtr.Zero;
        }
        _initialized = false;
        _readyForInit = false;
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
            throw new PlatformNotSupportedException("AAudio 输出仅支持 Android（API 27+）。");
    }

    // ── NDK P/Invoke（libaaudio.so，AAudio.h）──

    [LibraryImport("libaaudio.so")]
    private static partial int AAudio_createStreamBuilder(out IntPtr builder);

    [LibraryImport("libaaudio.so")]
    private static partial void AAudioStreamBuilder_setDirection(IntPtr builder, int direction);

    [LibraryImport("libaaudio.so")]
    private static partial void AAudioStreamBuilder_setSampleRate(IntPtr builder, int sampleRate);

    [LibraryImport("libaaudio.so")]
    private static partial void AAudioStreamBuilder_setChannelCount(IntPtr builder, int channelCount);

    [LibraryImport("libaaudio.so")]
    private static partial void AAudioStreamBuilder_setFormat(IntPtr builder, int format);

    [LibraryImport("libaaudio.so")]
    private static partial void AAudioStreamBuilder_setPerformanceMode(IntPtr builder, int mode);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStreamBuilder_openStream(IntPtr builder, out IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStreamBuilder_delete(IntPtr builder);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_requestStart(IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_requestPause(IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_requestFlush(IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_requestStop(IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_close(IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_write(IntPtr stream, IntPtr buffer, int numFrames, long timeoutNanoseconds);

    [LibraryImport("libaaudio.so")]
    private static partial long AAudioStream_getFramesRead(IntPtr stream);

    [LibraryImport("libaaudio.so")]
    private static partial int AAudioStream_getBufferSizeInFrames(IntPtr stream);
}
