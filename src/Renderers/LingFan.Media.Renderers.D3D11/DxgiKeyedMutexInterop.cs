using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// <c>IDXGIKeyedMutex</c> 裸 vtable 互操作。
/// </summary>
/// <remarks>
/// <para><b>为什么需要（修 Vortice <c>AcquireSync</c> 返回 void 的超时盲区）</b>：
/// Vortice 的 <c>IDXGIKeyedMutex.AcquireSync(UInt64, Int32)</c> 声明为 <b>返回 <c>void</c></b>，
/// 其底层 HRESULT 被 SharpGen 吞掉。keyed mutex 超时返回 <c>WAIT_TIMEOUT</c>=<c>0x102</c>（严重位为 0，
/// 被视作成功）→ 调用方无法感知「没拿到锁」→ 释放一把没获取的锁 → 跨设备数据竞争甚至崩溃。</para>
/// <para>本类用裸 vtable 取真实 HRESULT，使生产者能感知超时并以「丢弃本帧」优雅降级，而非阻塞管线线程。</para>
/// <para><b>IID 与 vtable 布局（逐字节核对 dxgi.h 实物）</b>：</para>
/// <list type="bullet">
/// <item><c>IID_IDXGIKeyedMutex</c> = <c>{9D8E1289-7B92-49EC-8441-BA727E52320F}</c>。</item>
/// <item>继承链 <c>IDXGIKeyedMutex → IDXGIDeviceSubObject → IDXGIObject → IUnknown</c>，
/// 故 vtable 槽位：0 QI / 1 AddRef / 2 Release / 3 SetPrivateData / 4 SetPrivateDataInterface /
/// 5 GetPrivateData / 6 GetParent（IDXGIObject）/ 7 GetDevice（IDXGIDeviceSubObject）/
/// <b>8 AcquireSync / 9 ReleaseSync</b>（IDXGIKeyedMutex）。注意：不是从 3 起算——它是三层派生接口。</item>
/// </list>
/// <para><b>AOT 兼容</b>：零反射、零 <c>[ComImport]</c>，纯裸 vtable + <c>CallingConvention.Winapi</c>。</para>
/// </remarks>
internal static class DxgiKeyedMutexInterop
{
    /// <summary>
    /// <c>IID_IDXGIKeyedMutex</c>（dxgi.h）：
    /// <c>DEFINE_GUID(IID_IDXGIKeyedMutex, 0x9d8e1289, 0x7b92, 0x49ec, 0x84,0x41,0xba,0x72,0x7e,0x52,0x32,0x0f)</c>。
    /// </summary>
    private static readonly Guid IID_IDXGIKeyedMutex =
        new(0x9d8e1289, 0x7b92, 0x49ec, 0x84, 0x41, 0xba, 0x72, 0x7e, 0x52, 0x32, 0x0f);

    private const int VtblSlotAcquireSync = 8;
    private const int VtblSlotReleaseSync = 9;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int AcquireSyncFn(IntPtr self, ulong key, int milliseconds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int ReleaseSyncFn(IntPtr self, ulong key);

    /// <summary>
    /// 在纹理 COM 指针上 QI 出 <c>IDXGIKeyedMutex</c> 裸接口指针（调用方负责 <see cref="Marshal.Release"/>）。
    /// </summary>
    /// <returns>成功返回 keyed mutex 指针；不支持时返回 <see cref="IntPtr.Zero"/>。</returns>
    internal static IntPtr QueryInterface(IntPtr texturePtr)
    {
        if (texturePtr == IntPtr.Zero)
            return IntPtr.Zero;

        int hr = Marshal.QueryInterface(texturePtr, in IID_IDXGIKeyedMutex, out IntPtr km);
        return hr < 0 || km == IntPtr.Zero ? IntPtr.Zero : km;
    }

    /// <summary>从 keyed mutex 裸指针取 <c>AcquireSync</c> 委托（vtable 槽位 8）。</summary>
    internal static AcquireSyncFn? GetAcquireDelegate(IntPtr kmPtr)
    {
        if (kmPtr == IntPtr.Zero)
            return null;
        IntPtr vtbl = Marshal.ReadIntPtr(kmPtr);
        IntPtr slot = Marshal.ReadIntPtr(vtbl, VtblSlotAcquireSync * IntPtr.Size);
        return slot == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<AcquireSyncFn>(slot);
    }

    /// <summary>从 keyed mutex 裸指针取 <c>ReleaseSync</c> 委托（vtable 槽位 9）。</summary>
    internal static ReleaseSyncFn? GetReleaseDelegate(IntPtr kmPtr)
    {
        if (kmPtr == IntPtr.Zero)
            return null;
        IntPtr vtbl = Marshal.ReadIntPtr(kmPtr);
        IntPtr slot = Marshal.ReadIntPtr(vtbl, VtblSlotReleaseSync * IntPtr.Size);
        return slot == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<ReleaseSyncFn>(slot);
    }
}
