namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// SwrContext 的 SafeHandle（音频重采样上下文）。
/// </summary>
/// <remarks>
/// 释放函数：<c>swr_free</c>——释放音频重采样上下文。
/// </remarks>
internal sealed class SafeSwrContextHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeSwrContextHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeSwrContextHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
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
                var pp = (SwrContext*)handle;
                FF.swr_free(&pp);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
