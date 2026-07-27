namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// AVBufferRef 的 SafeHandle。用于管理 FFmpeg 硬件设备上下文（hw_device_ctx）的引用计数。
/// </summary>
/// <remarks>
/// <para>释放函数：<c>av_buffer_unref</c>——释放引用计数，引用计数归零时释放底层资源。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——原生引用计数操作是快速同步调用，无 I/O await。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class SafeAVBufferRefHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeAVBufferRefHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeAVBufferRefHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
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
                var pp = (AVBufferRef*)handle;
                ffmpeg.av_buffer_unref(&pp);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
