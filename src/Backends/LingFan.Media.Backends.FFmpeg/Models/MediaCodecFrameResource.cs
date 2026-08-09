using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Models;

/// <summary>
/// Android MediaCodec 表面直渲染帧资源。
/// </summary>
/// <remarks>
/// <para>表面模式下 MediaCodec 硬解输出 <c>AV_PIX_FMT_MEDIACODEC</c> 帧：
/// <c>data[3]</c> = <c>AVMediaCodecBuffer*</c>，像素数据驻留在 GPU/SurfaceTexture，CPU 不可访问。</para>
/// <para><b>送显</b>：渲染层匹配到本类型后调用 <see cref="Render"/> —— 内部
/// <c>av_mediacodec_release_buffer(buffer, 1)</c> 将该帧送显到解码器绑定的 Surface（零拷贝）。</para>
/// <para><b>所有权</b>：持有 <c>av_frame_clone</c> 克隆帧（引用计数）保活 AVMediaCodecBuffer；
/// <see cref="Dispose"/> 时若未送显则以 <c>render=0</c> 归还缓冲，再释放克隆帧。仅可释放一次（幂等）。</para>
/// <para><b>格式说明</b>：<see cref="Format"/> 报告 <see cref="PixelFormat.NV12"/>（MediaCodec 解码底层格式约定），
/// 但数据不可 CPU 映射——消费方必须按类型（pattern matching）识别本资源，不得按软件帧读取。</para>
/// <para><b>线程安全</b>：Render/Dispose 需在同一消费线程调用，非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，SafeHandle + P/Invoke，零反射。</para>
/// </remarks>
internal sealed class MediaCodecFrameResource : IFrameResource
{
    private readonly SafeAVFrameHandle _frameOwner;
    private IntPtr _buffer;
    private bool _rendered;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="MediaCodecFrameResource"/> 的新实例。
    /// </summary>
    /// <param name="buffer">AVMediaCodecBuffer 指针（克隆帧 data[3]）。</param>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="frameOwner">克隆 AVFrame 的 SafeHandle（保活 AVMediaCodecBuffer）。</param>
    public MediaCodecFrameResource(IntPtr buffer, int width, int height, SafeAVFrameHandle frameOwner)
    {
        if (buffer == IntPtr.Zero)
            throw new ArgumentException("AVMediaCodecBuffer 指针不能为空。", nameof(buffer));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(frameOwner);

        _buffer = buffer;
        Width = width;
        Height = height;
        _frameOwner = frameOwner;
    }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format => PixelFormat.NV12;

    /// <summary>
    /// 将帧送显到解码器绑定的 Surface（<c>av_mediacodec_release_buffer(buffer, 1)</c>，零拷贝）。
    /// 每帧仅可送显一次；送显后缓冲即归还 MediaCodec，不可再次调用。
    /// </summary>
    /// <exception cref="ObjectDisposedException">资源已释放。</exception>
    /// <exception cref="InvalidOperationException">帧已送显。</exception>
    public void Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendered)
            throw new InvalidOperationException("MediaCodec 帧已送显，缓冲已归还，不可重复渲染。");
        _rendered = true;
        _ = MediaCodecInterop.ReleaseBuffer(_buffer, 1);
        _buffer = IntPtr.Zero;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 未送显的缓冲以 render=0 归还（否则 MediaCodec 输出缓冲耗尽会卡死解码器）
        if (!_rendered && _buffer != IntPtr.Zero)
        {
            try { _ = MediaCodecInterop.ReleaseBuffer(_buffer, 0); }
            catch { /* 忽略释放错误 */ }
            _buffer = IntPtr.Zero;
        }

        _frameOwner.Dispose();
    }
}
