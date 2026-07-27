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
/// <para><b>AOT 兼容</b>：sealed 类，<c>[ComImport]</c> 零反射，<c>[LibraryImport]</c> 源生成 P/Invoke。</para>
/// <para><b>分工</b>：<see cref="Platforms.Windows.DirectCompositionInterop"/> 供外部消费者（Avalonia 层等）使用；
/// 本类为 D3D11 渲染器内部专用，避免跨模块引用。两者 COM 接口定义相同但独立维护。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class D3D11CompositionInterop : IDisposable
{
    [ComImport]
    [Guid("C37EA93A-E7AA-450D-B16F-9746CB0406F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionDevice
    {
        void Commit();                                              // slot 3
        void WaitForCommitCompletion();                             // slot 4
        void GetFrameStatistics(IntPtr statistics);                 // slot 5
        void CreateTargetForHwnd(IntPtr hwnd, bool topmost, out IntPtr target); // slot 6
        void CreateVisual(out IntPtr visual);                       // slot 7
    }

    [ComImport]
    [Guid("4D93059D-097B-4651-9B60-DF5B5F9B6FF5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionVisual
    {
        void SetOffsetX(float offsetX);                             // slot 3
        void SetOffsetX2(IntPtr animation);                        // slot 4
        void SetOffsetY(float offsetY);                             // slot 5
        void SetOffsetY2(IntPtr animation);                        // slot 6
        void SetTransform(IntPtr transform);                       // slot 7
        void SetTransform2(IntPtr matrix);                         // slot 8
        void SetTransformParent(IntPtr visual);                     // slot 9
        void SetClip(IntPtr clip);                                 // slot 10
        void SetClip2(IntPtr rect);                                // slot 11
        void SetContent(IntPtr content);                           // slot 12
    }

    [ComImport]
    [Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionTarget
    {
        void SetRoot(IntPtr visual);                               // slot 3
    }

    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0406F3");

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice(
        IntPtr renderingDevice, in Guid iid, out IntPtr dcompositionDevice);

    private IDCompositionDevice? _device;
    private IDCompositionVisual? _visual;
    private IDCompositionTarget? _target;

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
            _device = (IDCompositionDevice)Marshal.GetObjectForIUnknown(devicePtr);
            Marshal.Release(devicePtr);

            _device.CreateVisual(out IntPtr visualPtr);
            _visual = (IDCompositionVisual)Marshal.GetObjectForIUnknown(visualPtr);
            Marshal.Release(visualPtr);
            _visual.SetContent(swapChainPtr);

            _device.CreateTargetForHwnd(hwnd, true, out IntPtr targetPtr);
            if (targetPtr == IntPtr.Zero)
                return false;
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

            _device.Commit();
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
        if (_target is not null)
        {
            try { _target.SetRoot(IntPtr.Zero); } catch { /* 忽略 */ }
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
            try { _device.Commit(); } catch { /* 忽略 */ }
            Marshal.ReleaseComObject(_device);
            _device = null;
        }
    }
}
