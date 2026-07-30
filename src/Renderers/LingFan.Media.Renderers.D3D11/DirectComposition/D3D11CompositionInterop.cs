using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Renderers.D3D11.DirectComposition;

/// <summary>
/// D3D11 模块私有的 DirectComposition 互操作（无空域渲染）。
/// </summary>
/// <remarks>
/// <para><b>无空域原理</b>：SwapChain 用 <c>CreateSwapChainForComposition</c> 创建（非 <c>CreateSwapChainForHwnd</c>），
/// 通过 DComp Visual 合成到窗口——视频帧不作为独立原生窗口，而是作为 GPU 纹理合入 Avalonia 主渲染管线。</para>
/// <para><b>异步策略</b>：全部同步（sync/native 分类）——COM 调用为快速同步操作，无 I/O。</para>
/// <para><b>线程安全</b>：须在 UI 线程调用。</para>
/// <para><b>AOT 兼容</b>：sealed 类，采用原始 vtable P/Invoke（ComVTable 委托封送），不使用 <c>[ComImport]</c>/RCW，
/// <c>NativeAOT</c> 兼容。</para>
/// <para><b>分工</b>：<see cref="LingFan.Media.Platforms.Windows.DirectCompositionInterop"/> 供外部消费者（Avalonia 层等）使用；
/// 本类为 D3D11 渲染器内部专用，避免跨模块引用。两者 COM 接口定义相同但独立维护。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class D3D11CompositionInterop : IDisposable
{
    // ── COM vtable 委托（AOT 兼容：纯 P/Invoke + 委托封送，不使用 [ComImport]/RCW）──
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int IDCompositionDevice_Commit(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int IDCompositionDevice_CreateTargetForHwnd(
        IntPtr self, IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int IDCompositionDevice_CreateVisual(IntPtr self, out IntPtr visual);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int IDCompositionVisual_SetContent(IntPtr self, IntPtr content);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int IDCompositionTarget_SetRoot(IntPtr self, IntPtr visual);

    /// <summary>
    /// 从 COM 接口指针读取第 (3 + slotIndex) 个 vtable 槽位的函数指针并转为强类型委托。
    /// </summary>
    private static class DCompVTable
    {
        public static TDelegate Get<TDelegate>(IntPtr comPtr, int slotIndex) where TDelegate : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(comPtr);
            IntPtr methodPtr = Marshal.ReadIntPtr(vtable, (3 + slotIndex) * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
        }
    }

    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0406F3");

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice(
        IntPtr renderingDevice, in Guid iid, out IntPtr dcompositionDevice);

    private IntPtr _device;   // IDCompositionDevice*
    private IntPtr _visual;   // IDCompositionVisual*
    private IntPtr _target;   // IDCompositionTarget*
    private bool _disposed;

    /// <summary>
    /// 初始化 DirectComposition 并将 SwapChain 绑定到指定窗口。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="swapChainPtr">IDXGISwapChain COM 指针（须以 CreateSwapChainForComposition 创建）。</param>
    /// <returns>成功返回 true，失败返回 false（调用方回退到 CreateSwapChainForHwnd）。</returns>
    internal bool TryInitialize(IntPtr hwnd, IntPtr swapChainPtr)
    {
        try
        {
            int hr = DCompositionCreateDevice(IntPtr.Zero, in IID_IDCompositionDevice, out IntPtr devicePtr);
            if (hr < 0 || devicePtr == IntPtr.Zero)
                return false;

            // 取得原始 COM 指针所有权（refcount=1，由本类在 Dispose 时 Marshal.Release）
            _device = devicePtr;

            DCompVTable.Get<IDCompositionDevice_CreateVisual>(_device, 4)(_device, out IntPtr visualPtr);
            _visual = visualPtr;
            // DComp-1：SetContent 相对槽应为 12（绝对槽 15）。原 slotIndex 9 命中 SetBorderMode，
            // 导致无空域合成静默失效。增加 HR 检查，失败则回退 HWND 模式（调用方 Dispose 本对象）。
            int setContentHr = DCompVTable.Get<IDCompositionVisual_SetContent>(_visual, 12)(_visual, swapChainPtr);
            if (setContentHr < 0)
                return false;

            DCompVTable.Get<IDCompositionDevice_CreateTargetForHwnd>(_device, 3)(_device, hwnd, true, out IntPtr targetPtr);
            if (targetPtr == IntPtr.Zero)
                return false;
            _target = targetPtr;

            DCompVTable.Get<IDCompositionTarget_SetRoot>(_target, 0)(_target, _visual);

            DCompVTable.Get<IDCompositionDevice_Commit>(_device, 0)(_device);
            return true;
        }
        catch
        {
            // DirectComposition 不可用（旧版 Windows 或无桌面合成）——回退到 HWND SwapChain
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// 释放 DirectComposition 资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_target != IntPtr.Zero)
        {
            try { DCompVTable.Get<IDCompositionTarget_SetRoot>(_target, 0)(_target, IntPtr.Zero); }
            catch { /* 忽略 */ }
            Marshal.Release(_target);
            _target = IntPtr.Zero;
        }
        if (_visual != IntPtr.Zero)
        {
            Marshal.Release(_visual);
            _visual = IntPtr.Zero;
        }
        if (_device != IntPtr.Zero)
        {
            try { DCompVTable.Get<IDCompositionDevice_Commit>(_device, 0)(_device); }
            catch { /* 忽略 */ }
            Marshal.Release(_device);
            _device = IntPtr.Zero;
        }
    }
}
