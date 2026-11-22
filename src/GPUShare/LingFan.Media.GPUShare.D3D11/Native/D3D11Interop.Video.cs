using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Interop;

namespace LingFan.Media.GPUShare.D3D11;

// D3D11Interop 的 Video 功能域公开互操作原语（部分类，由 D3D11Interop.cs 按域拆分，编译期等价）。
// IID/槽位常量、PFN 委托与平面导出函数在 D3D11Interop.cs；vtable 槽位不可手算、须运行时校准。
public static unsafe partial class D3D11Interop
{
    /// <summary>ID3D11VideoDevice::CreateVideoProcessorEnumerator（绝对槽位 10）。</summary>
    public static IntPtr CreateVideoProcessorEnumerator(IntPtr videoDevicePtr, in D3D11VideoProcessorContentDescription desc)
    {
        D3D11VideoProcessorContentDescription d = desc;
        D3D11VideoProcessorContentDescription* p = &d;
        var fn = ComVTable.GetAbsolute<PFN_CreateVideoProcessorEnumerator>(videoDevicePtr, 10);
        int hr = fn(videoDevicePtr, (IntPtr)p, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"CreateVideoProcessorEnumerator 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessor（绝对槽位 4）。</summary>
    public static IntPtr CreateVideoProcessor(IntPtr videoDevicePtr, IntPtr enumeratorPtr, uint contentDescIndex)
    {
        var fn = ComVTable.GetAbsolute<PFN_CreateVideoProcessor>(videoDevicePtr, 4);
        int hr = fn(videoDevicePtr, enumeratorPtr, contentDescIndex, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"CreateVideoProcessor 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>ID3D11VideoProcessorEnumerator::GetVideoProcessorCaps（绝对槽位 9）。</summary>
    public static D3D11VideoProcessorCaps GetVideoProcessorCaps(IntPtr enumeratorPtr)
    {
        D3D11VideoProcessorCaps caps = default;
        D3D11VideoProcessorCaps* p = &caps;
        var fn = ComVTable.GetAbsolute<PFN_GetVideoProcessorCaps>(enumeratorPtr, 9);
        int hr = fn(enumeratorPtr, (IntPtr)p);
        if (hr < 0)
            throw new COMException($"GetVideoProcessorCaps 失败 (0x{hr:X8})", hr);
        return caps;
    }

    /// <summary>
    /// ID3D11VideoProcessorEnumerator::CheckVideoProcessorFormat（绝对槽位 8）。
    /// 返回格式支持标志（D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT：INPUT=0x1 / OUTPUT=0x2）。
    /// </summary>
    public static uint CheckVideoProcessorFormat(IntPtr enumeratorPtr, uint format)
    {
        uint flags = 0;
        uint* p = &flags;
        var fn = ComVTable.GetAbsolute<PFN_CheckVideoProcessorFormat>(enumeratorPtr, 8);
        int hr = fn(enumeratorPtr, format, (IntPtr)p);
        if (hr < 0)
            throw new COMException($"CheckVideoProcessorFormat(0x{format:X}) 失败 (0x{hr:X8})", hr);
        return flags;
    }

    /// <summary>
    /// ID3D11VideoProcessorEnumerator::GetVideoProcessorContentDesc（绝对槽位 7）。
    /// 读回枚举器创建时的内容描述（用于验证枚举器槽位链正确性）。
    /// </summary>
    public static D3D11VideoProcessorContentDescription GetVideoProcessorContentDesc(IntPtr enumeratorPtr)
    {
        D3D11VideoProcessorContentDescription desc = default;
        D3D11VideoProcessorContentDescription* p = &desc;
        var fn = ComVTable.GetAbsolute<PFN_GetVideoProcessorContentDesc>(enumeratorPtr, 7);
        int hr = fn(enumeratorPtr, (IntPtr)p);
        if (hr < 0)
            throw new COMException($"GetVideoProcessorContentDesc 失败 (0x{hr:X8})", hr);
        return desc;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorInputView（绝对槽位 8）。</summary>
    public static IntPtr CreateVideoProcessorInputView(
        IntPtr videoDevicePtr, IntPtr resourcePtr, IntPtr enumeratorPtr, in D3D11VideoProcessorInputViewDesc desc)
    {
        D3D11VideoProcessorInputViewDesc d = desc;
        D3D11VideoProcessorInputViewDesc* p = &d;
        var fn = ComVTable.GetAbsolute<PFN_CreateVideoProcessorInputView>(videoDevicePtr, 8);
        int hr = fn(videoDevicePtr, resourcePtr, enumeratorPtr, (IntPtr)p, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"CreateVideoProcessorInputView 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorOutputView（绝对槽位 9）。</summary>
    public static IntPtr CreateVideoProcessorOutputView(
        IntPtr videoDevicePtr, IntPtr resourcePtr, IntPtr enumeratorPtr, in D3D11VideoProcessorOutputViewDesc desc)
    {
        D3D11VideoProcessorOutputViewDesc d = desc;
        D3D11VideoProcessorOutputViewDesc* p = &d;
        var fn = ComVTable.GetAbsolute<PFN_CreateVideoProcessorOutputView>(videoDevicePtr, 9);
        int hr = fn(videoDevicePtr, resourcePtr, enumeratorPtr, (IntPtr)p, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"CreateVideoProcessorOutputView 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>ID3D11VideoContext::VideoProcessorBlt（绝对槽位 53，NV12→RGBA 硬件视频处理器）。</summary>
    public static void VideoProcessorBlt(
        IntPtr videoContextPtr, IntPtr processor, IntPtr outputView, uint outputFrame, in D3D11VideoProcessorStream stream)
    {
        D3D11VideoProcessorStream s = stream;
        D3D11VideoProcessorStream* p = &s;
        var fn = ComVTable.GetAbsolute<PFN_VideoProcessorBlt>(videoContextPtr, 53);
        int hr = fn(videoContextPtr, processor, outputView, outputFrame, 1, (IntPtr)p);
        if (hr < 0)
            throw new COMException($"VideoProcessorBlt 失败 (0x{hr:X8})", hr);
    }

    /// <summary>
    /// ID3D11VideoContext::VideoProcessorSetStreamFrameFormat（绝对槽位 27，void 返回）。
    /// <para>VideoProcessorBlt 的<b>前置必需状态</b>：为指定输入流设置帧格式（逐行/隔行）。
    /// 所有参考实现（VLC/Chromium/MF）都在 Blt 前调用；缺失会使部分驱动在 Blt 报 E_INVALIDARG。</para>
    /// </summary>
    /// <param name="videoContextPtr">ID3D11VideoContext 指针。</param>
    /// <param name="processor">ID3D11VideoProcessor 指针。</param>
    /// <param name="streamIndex">输入流索引（本仓恒 0，单流）。</param>
    /// <param name="frameFormat">D3D11_VIDEO_FRAME_FORMAT（0=逐行扫描 PROGRESSIVE）。</param>
    public static void VideoProcessorSetStreamFrameFormat(IntPtr videoContextPtr, IntPtr processor, uint streamIndex, uint frameFormat)
    {
        var fn = ComVTable.GetAbsolute<PFN_VideoProcessorSetStreamFrameFormat>(videoContextPtr, 27);
        fn(videoContextPtr, processor, streamIndex, frameFormat);
    }

    /// <summary>
    /// ID3D11VideoContext::VideoProcessorSetStreamColorSpace（绝对槽位 28，void 返回）。
    /// <para>设置指定输入流的颜色空间（YCbCr 矩阵/标称范围），Blt 前置状态。</para>
    /// </summary>
    public static void VideoProcessorSetStreamColorSpace(IntPtr videoContextPtr, IntPtr processor, uint streamIndex, in D3D11VideoProcessorColorSpace colorSpace)
    {
        D3D11VideoProcessorColorSpace cs = colorSpace;
        D3D11VideoProcessorColorSpace* p = &cs;
        var fn = ComVTable.GetAbsolute<PFN_VideoProcessorSetStreamColorSpace>(videoContextPtr, 28);
        fn(videoContextPtr, processor, streamIndex, (IntPtr)p);
    }

    /// <summary>
    /// ID3D11VideoContext::VideoProcessorSetOutputColorSpace（绝对槽位 15，void 返回）。
    /// <para>设置输出的颜色空间（RGB 范围/标称范围），Blt 前置状态。</para>
    /// </summary>
    public static void VideoProcessorSetOutputColorSpace(IntPtr videoContextPtr, IntPtr processor, in D3D11VideoProcessorColorSpace colorSpace)
    {
        D3D11VideoProcessorColorSpace cs = colorSpace;
        D3D11VideoProcessorColorSpace* p = &cs;
        var fn = ComVTable.GetAbsolute<PFN_VideoProcessorSetOutputColorSpace>(videoContextPtr, 15);
        fn(videoContextPtr, processor, (IntPtr)p);
    }

    /// <summary>
    /// ID3D11VideoContext::VideoProcessorSetStreamSourceRect（绝对槽位 30，void 返回）。
    /// <para>设置指定输入流的源矩形（裁剪输入纹理的编解码对齐填充，取左上角实际视频区域）。</para>
    /// </summary>
    /// <param name="videoContextPtr">ID3D11VideoContext 指针。</param>
    /// <param name="processor">ID3D11VideoProcessor 指针。</param>
    /// <param name="streamIndex">输入流索引（本仓恒 0）。</param>
    /// <param name="enable">是否启用自定义源矩形（1=启用）。</param>
    /// <param name="rect">源矩形（帧坐标系：左上角对齐的帧尺寸区域）。</param>
    public static void VideoProcessorSetStreamSourceRect(IntPtr videoContextPtr, IntPtr processor, uint streamIndex, int enable, in D3D11Rect rect)
    {
        D3D11Rect r = rect;
        D3D11Rect* p = &r;
        var fn = ComVTable.GetAbsolute<PFN_VideoProcessorSetStreamSourceRect>(videoContextPtr, 30);
        fn(videoContextPtr, processor, streamIndex, enable, (IntPtr)p);
    }

    /// <summary>
    /// 为视频解码创建 D3D11 设备（DriverType=Hardware + BgraSupport|VideoSupport）。
    /// DXVA 硬解硬性要求 VideoSupport（解码 MFT 经 IMFDXGIDeviceManager 绑定设备），
    /// 与 <see cref="D3D11CreateDevice(out IntPtr, out IntPtr)"/> 的纯渲染用途区分。
    /// </summary>
    /// <remarks>返回的 device/context 引用计数已为 1，调用方须显式 <see cref="Release"/>。</remarks>
    public static void D3D11CreateDeviceForVideo(out IntPtr device, out IntPtr context)
    {
        int hr = D3D11CreateDeviceNative(
            IntPtr.Zero, D3D11_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out device, IntPtr.Zero, out context);
        if (hr < 0)
            throw new COMException($"D3D11CreateDevice(ForVideo) 失败 (0x{hr:X8})", hr);
    }
}
