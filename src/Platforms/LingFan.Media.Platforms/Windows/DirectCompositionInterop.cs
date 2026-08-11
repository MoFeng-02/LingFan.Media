using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// Windows DirectComposition 互操作——将 SwapChain 合成到窗口 Visual 树（无空域）。
/// </summary>
/// <remarks>
/// <para><b>无空域原理</b>：视频帧不作为独立原生窗口渲染，而是作为 GPU 纹理，
/// 通过 DirectComposition 的 IDCompositionVisual 合成到 Avalonia 窗口的 Visual 树中。
/// SwapChain 使用 <c>CreateSwapChainForComposition</c>（非 <c>CreateSwapChainForHwnd</c>），
/// 由 DComp 合成器整合到主渲染管线。</para>
/// <para><b>异步策略</b>：全部同步（sync/native 分类）——DirectComposition COM 调用为快速同步操作，无 I/O await。
/// 包 <c>async</c> 方法体内无 <c>await</c> 即伪异步，禁止。</para>
/// <para><b>线程安全</b>：所有方法须在 UI 线程调用（COM apartment 要求）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，采用原始 vtable P/Invoke（ComVTable 委托封送），不使用 <c>[ComImport]</c>/RCW，
/// <c>NativeAOT</c> 兼容；<c>[LibraryImport]</c> 源生成 P/Invoke。
/// <c>[SupportedOSPlatform("windows")]</c> 标注平台限制。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class DirectCompositionInterop : IDisposable
{
    // ── COM vtable 委托（AOT 兼容：纯 P/Invoke + 委托封送，不使用 [ComImport]/RCW）──
    // DirectComposition 接口 vtable 布局：IUnknown(0=QueryInterface, 1=AddRef, 2=Release) + 接口方法(3+)。
    // 委托首个参数为 COM 对象指针（this）；DCompVTable.Get 从绝对 vtable 槽位（3 + slotIndex）取函数指针。
    // 仅声明实际被调用的方法；槽位按 Windows SDK dcomp.h 真实顺序排列。

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IDCompositionDevice_Commit(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IDCompositionDevice_CreateTargetForHwnd(
        IntPtr self, IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IDCompositionDevice_CreateVisual(IntPtr self, out IntPtr visual);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IDCompositionVisual_SetContent(IntPtr self, IntPtr content);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IDCompositionTarget_SetRoot(IntPtr self, IntPtr visual);

    /// <summary>
    /// 从 COM 接口指针读取第 (3 + slotIndex) 个 vtable 槽位的函数指针并转为强类型委托。
    /// IUnknown 的 Release 在槽 2（调用方用 <see cref="Marshal.Release"/> 释放，与 WASAPI/MF 一致，非 RCW）。
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

    // ── P/Invoke ──

    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0406F3");

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice(
        IntPtr renderingDevice,
        in Guid iid,
        out IntPtr dcompositionDevice);

    // ── 状态（COM 对象以原始指针持有，释放用 Marshal.Release）──

    private IntPtr _device;   // IDCompositionDevice*
    private IntPtr _visual;   // IDCompositionVisual*
    private IntPtr _target;   // IDCompositionTarget*
    private bool _disposed;

    /// <summary>
    /// 初始化 DirectComposition 并将 SwapChain 绑定到指定窗口（无空域）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="swapChainPtr">IDXGISwapChain COM 指针（须以 CreateSwapChainForComposition 创建）。</param>
    /// <exception cref="InvalidOperationException">DirectComposition 初始化失败。</exception>
    public void Initialize(IntPtr hwnd, IntPtr swapChainPtr)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("窗口句柄无效。", nameof(hwnd));
        if (swapChainPtr == IntPtr.Zero)
            throw new ArgumentException("SwapChain 指针无效。", nameof(swapChainPtr));

        // 修复：重置 _disposed 标志，允许 Initialize 失败后重试
        // （catch 块调用 Dispose 会设 _disposed=true，若不重置，成功重试后的 Dispose 会被跳过 → COM 泄漏）
        _disposed = false;

        // 1. 创建 DCompositionDevice（不关联渲染设备，使用系统默认）
        int hr = DCompositionCreateDevice(IntPtr.Zero, in IID_IDCompositionDevice, out IntPtr devicePtr);
        if (hr < 0 || devicePtr == IntPtr.Zero)
            throw new InvalidOperationException($"DCompositionCreateDevice 失败: HRESULT 0x{hr:X8}");

        // 取得原始 COM 指针所有权（refcount=1，由本类在 Dispose 时 Marshal.Release）
        _device = devicePtr;

        try
        {
            // 2. 创建 Visual 并绑定 SwapChain
            DCompVTable.Get<IDCompositionDevice_CreateVisual>(_device, 4)(_device, out IntPtr visualPtr);
            _visual = visualPtr;
            // DComp-1：SetContent 相对槽应为 12（绝对槽 15）。原 slotIndex 9 命中 SetBorderMode，
            // 导致无空域合成静默失效。增加 HR 检查，失败则抛异常（catch 块清理并向上传播）。
            int setContentHr = DCompVTable.Get<IDCompositionVisual_SetContent>(_visual, 12)(_visual, swapChainPtr);
            if (setContentHr < 0)
                throw new InvalidOperationException($"IDCompositionVisual.SetContent 失败: HRESULT 0x{setContentHr:X8}");

            // 3. 创建 Target 并绑定到窗口
            DCompVTable.Get<IDCompositionDevice_CreateTargetForHwnd>(_device, 3)(_device, hwnd, true, out IntPtr targetPtr);
            if (targetPtr == IntPtr.Zero)
                throw new InvalidOperationException("CreateTargetForHwnd 失败——窗口可能已被其他 DComp Target 绑定。");
            _target = targetPtr;

            // SetRoot 内部会对 visual AddRef；传入原始 visual 指针即可（无需 GetIUnknownForObject/RCW）
            DCompVTable.Get<IDCompositionTarget_SetRoot>(_target, 0)(_target, _visual);

            // 4. 提交
            DCompVTable.Get<IDCompositionDevice_Commit>(_device, 0)(_device);
        }
        catch
        {
            // 任何步骤失败时清理已创建的 COM 对象，防止泄漏
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// 释放 DirectComposition 资源（Visual/Target/Device）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 清除 Target 的根 Visual（断开与窗口的关联）
        if (_target != IntPtr.Zero)
        {
            try { DCompVTable.Get<IDCompositionTarget_SetRoot>(_target, 0)(_target, IntPtr.Zero); }
            catch { /* 忽略释放异常 */ }
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
            catch { /* 忽略提交异常 */ }
            Marshal.Release(_device);
            _device = IntPtr.Zero;
        }
    }
}
