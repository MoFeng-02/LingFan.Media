using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// <c>ID3D11Multithread</c> 互操作：为<b>共享</b> D3D11 设备开启多线程保护。
/// </summary>
/// <remarks>
/// <para><b>为什么必须开（解决「画面抽帧/跳场景 + 原生崩溃」）</b>：
/// <c>ID3D11DeviceContext</c>（immediate context）<b>不是线程安全的</b>。本项目的共享设备同时被两方使用：</para>
/// <list type="number">
/// <item>硬解线程：FFmpeg D3D11VA / MF DXVA 在解码线程上通过该 context 写解码输出纹理；</item>
/// <item>呈现线程：<c>D3D11Renderer</c> 在视频呈现线程上用同一 context 做 <c>CopySubresourceRegion</c> + <c>Present</c>。</item>
/// </list>
/// <para>FFmpeg 的 <c>AVD3D11VADeviceContext</c> 在 <c>av_hwdevice_ctx_init</c> 时会创建一把<b>自己的</b>互斥量
/// （<c>d3d11va_default_lock</c>）来保护它自己的 context 调用——但我方渲染器完全不参与那把锁，
/// 于是两条线程并发操作同一 immediate context ⇒ 命令流交错错乱（呈现出错帧/半写入帧）、
/// 长时间竞态后驱动状态破坏 ⇒ 原生 AccessViolation。</para>
/// <para><c>ID3D11Multithread::SetMultithreadProtected(TRUE)</c> 让 D3D11 运行时对该设备的所有
/// context 调用加内部临界区，使跨线程共享变为安全——这是共享设备做 DXVA/D3D11VA 零拷贝的<b>硬性前提</b>
/// （MSDN：<i>"If you use a single device for both video decoding and rendering from multiple threads,
/// you must enable multithread protection"</i>）。</para>
/// <para><b>IID 说明</b>：<c>ID3D11Multithread</c>（d3d11_4.h）与 <c>ID3D10Multithread</c>（d3d10.h）
/// <b>共用同一个 IID</b> <c>{9B7E4E00-342C-4106-A19F-4F2704F689F0}</c>，vtable 布局亦相同
/// （Enter / Leave / SetMultithreadProtected / GetMultithreadProtected）。此值已与两处 SDK 头文件逐字节核对。</para>
/// <para><b>AOT 兼容</b>：零反射、零 <c>[ComImport]</c>，纯原始 vtable + <c>CallingConvention.Winapi</c>
/// （COM 方法为 <c>STDMETHODCALLTYPE</c>，<c>this</c> 是普通栈上首参，绝不可用 <c>ThisCall</c>）。</para>
/// </remarks>
internal static class D3D11MultithreadInterop
{
    /// <summary>
    /// <c>IID_ID3D11Multithread</c> / <c>IID_ID3D10Multithread</c>（同值）。
    /// SDK 权威值：<c>DEFINE_GUID(IID_ID3D11Multithread, 0x9B7E4E00, 0x342C, 0x4106, 0xA1,0x9F,0x4F,0x27,0x04,0xF6,0x89,0xF0)</c>。
    /// </summary>
    private static readonly Guid IID_ID3D11Multithread =
        new(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);

    // ID3D11Multithread vtable（IUnknown 之后）：
    //   0 QueryInterface, 1 AddRef, 2 Release, 3 Enter, 4 Leave, 5 SetMultithreadProtected, 6 GetMultithreadProtected
    private const int VtblSlotSetMultithreadProtected = 5;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetMultithreadProtectedFn(IntPtr self, int bMTProtect);

    /// <summary>
    /// 尝试在给定 COM 对象（<c>ID3D11Device</c> 或 <c>ID3D11DeviceContext</c>）上开启多线程保护。
    /// </summary>
    /// <param name="unknown">目标 COM 对象的原始指针。</param>
    /// <returns>成功开启返回 <see langword="true"/>；对象不支持该接口返回 <see langword="false"/>。</returns>
    internal static bool TryEnable(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero)
            return false;

        Guid iid = IID_ID3D11Multithread;
        int hr = Marshal.QueryInterface(unknown, in iid, out IntPtr mt);
        if (hr < 0 || mt == IntPtr.Zero)
            return false;

        try
        {
            // 缓存必须先于调用：先取 vtable 槽位委托，再执行，避免坏指针上二次操作。
            IntPtr vtbl = Marshal.ReadIntPtr(mt);
            IntPtr slot = Marshal.ReadIntPtr(vtbl, VtblSlotSetMultithreadProtected * IntPtr.Size);
            if (slot == IntPtr.Zero)
                return false;

            var setProtected = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtectedFn>(slot);

            // 返回值是「调用前的旧状态」（BOOL），不是 HRESULT——不可用 hr<0 判定成败。
            setProtected(mt, 1);
            return true;
        }
        finally
        {
            // QueryInterface 成功获取一次 ⇒ 精确配对 Release 一次。
            Marshal.Release(mt);
        }
    }
}
