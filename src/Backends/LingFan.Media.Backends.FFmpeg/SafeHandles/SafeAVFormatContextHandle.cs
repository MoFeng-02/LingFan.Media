namespace LingFan.Media.Backends.FFmpeg.SafeHandles;

/// <summary>
/// AVFormatContext 的 SafeHandle。ReleaseHandle 由 CLR Critical Finalizer 保证执行。
/// </summary>
/// <remarks>
/// <para>AOT 兼容：SafeHandle 是 .NET 核心基础设施，不依赖反射。sealed 确保无虚表开销。</para>
/// <para>释放函数：<c>avformat_close_input</c>——关闭格式上下文并释放关联资源。</para>
/// </remarks>
internal sealed class SafeAVFormatContextHandle : SafeHandle
{
    /// <summary>初始化空的 SafeHandle。</summary>
    public SafeAVFormatContextHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>初始化并设置已有句柄。</summary>
    public SafeAVFormatContextHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
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
                var pp = (AVFormatContext*)handle;
                FF.avformat_close_input(&pp);
            }
            handle = IntPtr.Zero;
        }
        return true;
    }
}
