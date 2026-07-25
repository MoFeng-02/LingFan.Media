namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// AVPacket 的 SafeHandle。
/// </summary>
/// <remarks>
/// 释放函数：<c>av_packet_free</c>——释放原生包及其引用的数据缓冲。
/// </remarks>
internal sealed class SafeAVPacketHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeAVPacketHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeAVPacketHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
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
                var pp = (AVPacket*)handle;
                ffmpeg.av_packet_free(&pp);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
