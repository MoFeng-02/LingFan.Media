using System.ComponentModel;
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
/// <para><b>AOT 兼容</b>：sealed 类，<c>[ComImport]</c> 零反射，<c>[LibraryImport]</c> 源生成 P/Invoke。
/// <c>[SupportedOSPlatform("windows")]</c> 标注平台限制。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class DirectCompositionInterop : IDisposable
{
    // ── COM 接口定义（vtable 严格按 Windows SDK dcomp.h 声明顺序，不可省略方法）──

    /// <summary>IDCompositionDevice COM 接口。</summary>
    /// <remarks>
    /// vtable: 0-2 IUnknown(隐式), 3 Commit, 4 WaitForCommitCompletion, 5 GetFrameStatistics,
    /// 6 CreateTargetForHwnd, 7 CreateVisual。
    /// </remarks>
    [ComImport]
    [Guid("C37EA93A-E7AA-450D-B16F-9746CB0406F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionDevice
    {
        /// <summary>slot 3</summary>
        void Commit();
        /// <summary>slot 4</summary>
        void WaitForCommitCompletion();
        /// <summary>slot 5 — GetFrameStatistics(out DCOMPOSITION_FRAME_STATISTICS)，用 IntPtr 占位</summary>
        void GetFrameStatistics(IntPtr statistics);
        /// <summary>slot 6 — CreateTargetForHwnd(HWND, BOOL, out IDCompositionTarget)</summary>
        void CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
        /// <summary>slot 7 — CreateVisual(out IDCompositionVisual)</summary>
        void CreateVisual(out IntPtr visual);
    }

    /// <summary>IDCompositionVisual COM 接口。</summary>
    /// <remarks>
    /// vtable: 0-2 IUnknown(隐式), 3 SetOffsetX(float), 4 SetOffsetX(animation),
    /// 5 SetOffsetY(float), 6 SetOffsetY(animation), 7 SetTransform(transform),
    /// 8 SetTransform(matrix), 9 SetTransformParent, 10 SetClip(clip),
    /// 11 SetClip(rect), 12 SetContent。
    /// </remarks>
    [ComImport]
    [Guid("4D93059D-097B-4651-9B60-DF5B5F9B6FF5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionVisual
    {
        /// <summary>slot 3</summary>
        void SetOffsetX(float offsetX);
        /// <summary>slot 4</summary>
        void SetOffsetX2(IntPtr animation);
        /// <summary>slot 5</summary>
        void SetOffsetY(float offsetY);
        /// <summary>slot 6</summary>
        void SetOffsetY2(IntPtr animation);
        /// <summary>slot 7</summary>
        void SetTransform(IntPtr transform);
        /// <summary>slot 8 — const D2D_MATRIX_3X2_F&amp;</summary>
        void SetTransform2(IntPtr matrix);
        /// <summary>slot 9</summary>
        void SetTransformParent(IntPtr visual);
        /// <summary>slot 10</summary>
        void SetClip(IntPtr clip);
        /// <summary>slot 11 — const D2D_RECT_F&amp;</summary>
        void SetClip2(IntPtr rect);
        /// <summary>slot 12 — SetContent(IUnknown*)</summary>
        void SetContent(IntPtr content);
    }

    /// <summary>IDCompositionTarget COM 接口。</summary>
    /// <remarks>vtable: 0-2 IUnknown(隐式), 3 SetRoot。</remarks>
    [ComImport]
    [Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionTarget
    {
        /// <summary>slot 3 — SetRoot(IDCompositionVisual*)，null 清除根</summary>
        void SetRoot(IntPtr visual);
    }

    // ── P/Invoke ──

    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0406F3");

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice(
        IntPtr renderingDevice,
        in Guid iid,
        out IntPtr dcompositionDevice);

    // ── 状态 ──

    private IDCompositionDevice? _device;
    private IDCompositionVisual? _visual;
    private IDCompositionTarget? _target;
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

        // V2-15 第二轮审计修复：重置 _disposed 标志，允许 Initialize 失败后重试
        // （catch 块调用 Dispose 会设 _disposed=true，若不重置，成功重试后的 Dispose 会被跳过 → COM 泄漏）
        _disposed = false;

        // 1. 创建 DCompositionDevice（不关联渲染设备，使用系统默认）
        int hr = DCompositionCreateDevice(IntPtr.Zero, in IID_IDCompositionDevice, out IntPtr devicePtr);
        if (hr < 0 || devicePtr == IntPtr.Zero)
            throw new InvalidOperationException($"DCompositionCreateDevice 失败: HRESULT 0x{hr:X8}");

        try
        {
            _device = (IDCompositionDevice)Marshal.GetObjectForIUnknown(devicePtr);
            // GetObjectForIUnknown 会 AddRef，释放原始指针
            Marshal.Release(devicePtr);
            devicePtr = IntPtr.Zero; // 已释放，标记防止 finally 重复释放

            // 2. 创建 Visual 并绑定 SwapChain
            _device.CreateVisual(out IntPtr visualPtr);
            _visual = (IDCompositionVisual)Marshal.GetObjectForIUnknown(visualPtr);
            Marshal.Release(visualPtr);
            _visual.SetContent(swapChainPtr);

            // 3. 创建 Target 并绑定到窗口
            _device.CreateTargetForHwnd(hwnd, true, out IntPtr targetPtr);
            if (targetPtr == IntPtr.Zero)
                throw new InvalidOperationException("CreateTargetForHwnd 失败——窗口可能已被其他 DComp Target 绑定。");
            _target = (IDCompositionTarget)Marshal.GetObjectForIUnknown(targetPtr);
            Marshal.Release(targetPtr);

            // SetRoot 内部会对 visual AddRef，GetIUnknownForObject 返回的引用须由调用方释放
            IntPtr visualUnk = Marshal.GetIUnknownForObject(_visual);
            try
            {
                _target.SetRoot(visualUnk);
            }
            finally
            {
                Marshal.Release(visualUnk);
            }

            // 4. 提交
            _device.Commit();
        }
        catch
        {
            // 任何步骤失败时清理已创建的 COM 对象，防止泄漏
            Dispose();
            // 释放尚未交给 RCW 的原始指针（GetObjectForIUnknown 之前抛异常的情况）
            if (devicePtr != IntPtr.Zero)
                Marshal.Release(devicePtr);
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
        if (_target is not null)
        {
            try { _target.SetRoot(IntPtr.Zero); }
            catch { /* 忽略释放异常 */ }
            Marshal.ReleaseComObject(_target);
            _target = null;
        }

        if (_visual is not null)
        {
            Marshal.ReleaseComObject(_visual);
            _visual = null;
        }

        if (_device is not null)
        {
            try { _device.Commit(); }
            catch { /* 忽略提交异常 */ }
            Marshal.ReleaseComObject(_device);
            _device = null;
        }
    }
}
