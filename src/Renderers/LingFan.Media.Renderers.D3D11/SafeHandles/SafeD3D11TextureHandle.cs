namespace LingFan.Media.Renderers.D3D11.SafeHandles;

/// <summary>
/// D3D11 纹理的 SafeHandle。包装 ID3D11Texture2D COM 对象指针。
/// </summary>
/// <remarks>
/// <para>用于 D3D11TextureResource 持有的帧级 GPU 资源。
/// CLR Critical Finalizer 保证即使线程中止也能释放 COM 对象。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class SafeD3D11TextureHandle : SafeHandle
{
    /// <summary>
    /// 初始化 <see cref="SafeD3D11TextureHandle"/> 的新实例。
    /// </summary>
    public SafeD3D11TextureHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <summary>
    /// 从已有 COM 指针创建 SafeHandle（接管所有权）。
    /// </summary>
    /// <param name="existingHandle">ID3D11Texture2D 的 COM 指针。</param>
    public SafeD3D11TextureHandle(IntPtr existingHandle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            Marshal.Release(handle); // COM Release
            handle = IntPtr.Zero;
        }
        return true;
    }
}
