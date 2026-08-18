namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// SwsContext 的 SafeHandle（图像缩放/颜色空间转换上下文）。
/// </summary>
/// <remarks>
/// 释放函数：<c>sws_freeContext</c>——释放图像转换上下文。
/// </remarks>
internal sealed class SafeSwsContextHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeSwsContextHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeSwsContextHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
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
                var p = (SwsContext*)handle;
                FF.sws_freeContext(p);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
