namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// AVCodecContext 的 SafeHandle。
/// </summary>
/// <remarks>
/// 释放函数：<c>avcodec_free_context</c>——释放编解码上下文及其内部所有资源。
/// </remarks>
internal sealed class SafeAVCodecContextHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeAVCodecContextHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeAVCodecContextHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
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
                var pp = (AVCodecContext*)handle;
                FF.avcodec_free_context(&pp);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
