using LingFan.Media.Backends.MediaCodec.Interop;

namespace LingFan.Media.Backends.MediaCodec.Wrappers;

/// <summary>
/// NDK <c>AMediaCodec</c> 的托管包装（解码器）。
/// </summary>
/// <remarks>
/// <para>持有原生 <c>AMediaCodec*</c> 并在 <see cref="Dispose"/> 中释放。</para>
/// <para><b>ByteBuffer（CPU 输出）路径</b>：<c>configure</c> 的 <c>surface</c> 传 <see cref="nint.Zero"/>，
/// 解码输出经 <c>getOutputBuffer</c> 以 CPU 可读 buffer 返回（本后端当前唯一支持的路径；零拷贝 Surface/AHB
/// 路径尚未落地，由 <see cref="AndroidOptions.EnableHardwareBufferZeroCopy"/> 门控，未启用时自动回落此路径）。</para>
/// <para>解码循环与 FFmpeg 的 send/receive 同构：<c>dequeueInputBuffer</c> 申领输入槽 → 写入 →
/// <c>queueInputBuffer</c> 提交；<c>dequeueOutputBuffer</c> 申领输出槽（含
/// OUTPUT_FORMAT_CHANGED / TRY_AGAIN_LATER 等 INFO 语义）→ 读取 → <c>releaseOutputBuffer</c> 归还。</para>
/// </remarks>
internal sealed class AndroidMediaCodec : IDisposable
{
    private nint _native;

    private AndroidMediaCodec(nint native) => _native = native;

    /// <summary>按 MIME 创建解码器；失败抛 <see cref="InvalidOperationException"/>。</summary>
    public static AndroidMediaCodec CreateDecoderByType(string mime)
    {
        nint codec = MediaNdk.AMediaCodec_createDecoderByType(mime);
        if (codec == nint.Zero)
            throw new InvalidOperationException($"[ANDROID-CODEC] createDecoderByType('{mime}') 返回 null（设备或格式不支持）");
        return new AndroidMediaCodec(codec);
    }

    /// <summary>配置解码器（ByteBuffer 路径：surface = Zero，crypto = Zero，flags = 0）。</summary>
    public void Configure(AndroidMediaFormat format, nint surface, nint crypto, uint flags)
    {
        int s = MediaNdk.AMediaCodec_configure(_native, format.NativeHandle, surface, crypto, flags);
        if (s != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-CODEC] configure 失败: media_status_t={s}");
    }

    /// <summary>启动解码器。</summary>
    public void Start() => Check(MediaNdk.AMediaCodec_start(_native), "start");

    /// <summary>停止解码器。</summary>
    public void Stop() => Check(MediaNdk.AMediaCodec_stop(_native), "stop");

    /// <summary>丢弃全部在途输入/输出（seek 后必须调用）。</summary>
    public void Flush() => Check(MediaNdk.AMediaCodec_flush(_native), "flush");

    /// <summary>取输入 buffer 指针（失败返回 <see cref="nint.Zero"/>）。</summary>
    public nint GetInputBuffer(nuint idx, out nuint size)
        => MediaNdk.AMediaCodec_getInputBuffer(_native, idx, out size);

    /// <summary>取输出 buffer 指针（失败返回 <see cref="nint.Zero"/>）。</summary>
    public nint GetOutputBuffer(nuint idx, out nuint size)
        => MediaNdk.AMediaCodec_getOutputBuffer(_native, idx, out size);

    /// <summary>申领输入 buffer 索引（ssize_t）；返回 TRY_AGAIN_LATER(-1) 表示暂无可用。</summary>
    public nint DequeueInputBuffer(long timeoutUs)
        => MediaNdk.AMediaCodec_dequeueInputBuffer(_native, timeoutUs);

    /// <summary>提交输入 buffer（offset 为 off_t，time 为 PTS 微秒）。</summary>
    public void QueueInputBuffer(nuint idx, nint offset, nuint size, ulong time, uint flags)
        => Check(MediaNdk.AMediaCodec_queueInputBuffer(_native, idx, offset, size, time, flags), "queueInputBuffer");

    /// <summary>申领输出 buffer 索引（ssize_t）；负值取 AMEDIACODEC_INFO_* 语义。</summary>
    public nint DequeueOutputBuffer(out AMediaCodecBufferInfo info, long timeoutUs)
        => MediaNdk.AMediaCodec_dequeueOutputBuffer(_native, out info, timeoutUs);

    /// <summary>归还输出 buffer（render 非 0 时上屏，仅 Surface 模式有效）。</summary>
    public void ReleaseOutputBuffer(nuint idx, byte render)
        => Check(MediaNdk.AMediaCodec_releaseOutputBuffer(_native, idx, render), "releaseOutputBuffer");

    /// <summary>取输出格式（OUTPUT_FORMAT_CHANGED 后调用；调用方负责释放）。</summary>
    public AndroidMediaFormat GetOutputFormat()
    {
        nint fmt = MediaNdk.AMediaCodec_getOutputFormat(_native);
        if (fmt == nint.Zero)
            throw new InvalidOperationException("[ANDROID-CODEC] getOutputFormat 返回 null");
        return new AndroidMediaFormat(fmt);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_native == nint.Zero) return;
        MediaNdk.AMediaCodec_delete(_native);
        _native = nint.Zero;
    }

    private void Check(int status, string op)
    {
        if (status != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-CODEC] {op} 失败: media_status_t={status}");
    }
}
