namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// AVFrame 的 SafeHandle。
/// </summary>
/// <remarks>
/// 释放函数：<c>av_frame_free</c>——释放原生帧及其引用的数据缓冲。
/// </remarks>
internal sealed class SafeAVFrameHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeAVFrameHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeAVFrameHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            unsafe
            {
                var pp = (AVFrame*)handle;
                ffmpeg.av_frame_free(&pp);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
