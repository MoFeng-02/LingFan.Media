using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Interop;

namespace LingFan.Media.GPUShare.D3D11;

// D3D11Interop 的 Core 功能域公开互操作原语（部分类，由 D3D11Interop.cs 按域拆分，编译期等价）。
// IID/槽位常量、PFN 委托与平面导出函数在 D3D11Interop.cs；vtable 槽位不可手算、须运行时校准。
public static unsafe partial class D3D11Interop
{
    /// <summary>QueryInterface：失败抛 <see cref="COMException"/>。</summary>
    public static IntPtr QueryInterface(IntPtr comPtr, Guid iid)
    {
        var fn = ComVTable.GetAbsolute<PFN_QueryInterface>(comPtr, 0);
        int hr = fn(comPtr, ref iid, out IntPtr ppv);
        if (hr < 0)
            throw new COMException($"QueryInterface({iid:B}) 失败 (0x{hr:X8})", hr);
        return ppv;
    }

    /// <summary>QueryInterface：失败返回 <c>false</c>（不抛）。</summary>
    public static bool TryQueryInterface(IntPtr comPtr, Guid iid, out IntPtr ppv)
    {
        var fn = ComVTable.GetAbsolute<PFN_QueryInterface>(comPtr, 0);
        int hr = fn(comPtr, ref iid, out ppv);
        return hr >= 0 && ppv != IntPtr.Zero;
    }

    /// <summary>
    /// 诊断辅助：经 ID3D11DeviceChild::GetDevice（槽位 3）取对象所属设备指针。
    /// 失败返回 <c>IntPtr.Zero</c>（对象不支持 DeviceChild 或 QI 失败）。
    /// </summary>
    public static IntPtr GetDeviceChildDevice(IntPtr comPtr)
    {
        if (comPtr == IntPtr.Zero)
            return IntPtr.Zero;
        if (!TryQueryInterface(comPtr, IID_ID3D11DeviceChild, out IntPtr child))
            return IntPtr.Zero;
        try
        {
            var fn = ComVTable.GetAbsolute<PFN_GetDevice>(child, 3);
            fn(child, out IntPtr device);
            return device;
        }
        finally
        {
            Release(child);
        }
    }

    /// <summary>AddRef（返回新引用计数）。</summary>
    public static int AddRef(IntPtr comPtr) => ComVTable.GetAbsolute<PFN_AddRefRelease>(comPtr, 1)(comPtr);

    /// <summary>
    /// 经 ID3D11Texture2D::GetDesc（绝对槽位 10）读取纹理 DESC（只读，不改引用计数、无副作用）。
    /// 用于诊断解码侧硬解纹理的真实维度（ArraySize / BindFlags / Format / MiscFlags）。
    /// </summary>
    public static D3D11Texture2DDesc GetTexture2DDesc(IntPtr texture2DPtr)
    {
        if (texture2DPtr == IntPtr.Zero)
            throw new ArgumentException("texture2D 句柄不可为 Zero", nameof(texture2DPtr));
        var fn = ComVTable.GetAbsolute<PFN_GetTexture2DDesc>(texture2DPtr, 10);
        fn(texture2DPtr, out var desc);
        return desc;
    }

    /// <summary>Release（返回新引用计数；本模块全程仅持 IntPtr，显式 Release 规避双重释放）。</summary>
    public static int Release(IntPtr comPtr) => ComVTable.GetAbsolute<PFN_AddRefRelease>(comPtr, 2)(comPtr);

    /// <summary>ID3D11Device::CreateTexture2D（绝对槽位 5）。</summary>
    public static IntPtr CreateTexture2D(IntPtr devicePtr, in D3D11Texture2DDesc desc)
    {
        // 栈上局部变量取址（不经 fixed 语句）：跨标准 Roslyn / 本机编译器均合法，
        // 栈帧在方法返回前不被 GC 移动，与 fixed 语义等价。
        D3D11Texture2DDesc d = desc;
        D3D11Texture2DDesc* p = &d;
        var fn = ComVTable.GetAbsolute<PFN_CreateTexture2D>(devicePtr, 5);
        int hr = fn(devicePtr, (IntPtr)p, IntPtr.Zero, out IntPtr tex);
        if (hr < 0)
            throw new COMException($"CreateTexture2D 失败 (0x{hr:X8})", hr);
        return tex;
    }

    /// <summary>ID3D11DeviceContext::Flush（绝对槽位 111）。</summary>
    public static void Flush(IntPtr contextPtr)
    {
        var fn = ComVTable.GetAbsolute<PFN_Flush>(contextPtr, 111);
        fn(contextPtr);
    }

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion（绝对槽位 46）。</summary>
    /// <remarks>
    /// GPU 内资源拷贝：把 <paramref name="srcTexture"/> 的 <paramref name="srcSubresource"/> 切片完整拷入
    /// <paramref name="dstTexture"/> 的 <paramref name="dstSubresource"/>。不需要 SRV/RTV 绑定，
    /// 是「ffmpeg NV12 硬解切片 → 自建可共享 NV12 纹理」零拷贝导出路径的 D3D11 侧动作
    /// （NV12 硬解纹理不可绑 SRV，但可经 CopySubresourceRegion 拷出）。
    /// <paramref name="pSrcBox"/> 为 <see cref="IntPtr.Zero"/> 表示拷整张资源。
    /// </remarks>
    public static void CopySubresourceRegion(IntPtr contextPtr, IntPtr dstTexture, uint dstSubresource,
        uint dstX, uint dstY, uint dstZ, IntPtr srcTexture, uint srcSubresource, IntPtr pSrcBox)
    {
        var fn = ComVTable.GetAbsolute<PFN_CopySubresourceRegion>(contextPtr, 46);
        fn(contextPtr, dstTexture, dstSubresource, dstX, dstY, dstZ, srcTexture, srcSubresource, pSrcBox);
    }

    // ID3D11InfoQueue 委托（调试层消息捕获，vtable 槽位基于 d3d11.h IDL 序）。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PFN_GetNumStoredMessages(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetMessage(IntPtr self, uint index, IntPtr pMessage, ref ulong pMessageByteLength);

    /// <summary>
    /// 从 ID3D11InfoQueue 提取并打印全部存储的调试层消息（stderr）。
    /// <para>需设备以 D3D11_CREATE_DEVICE_DEBUG 创建；否则 QI ID3D11InfoQueue 会失败。</para>
    /// </summary>
    public static void DumpDebugMessages(IntPtr devicePtr)
    {
        if (!TryQueryInterface(devicePtr, IID_ID3D11InfoQueue, out IntPtr infoQueue))
        {
            Console.Error.WriteLine("[D3D11-DEBUG] InfoQueue 不可用（调试层未启用——Graphics Tools 可选功能未安装）");
            return;
        }
        try
        {
            var fnGetCount = ComVTable.GetAbsolute<PFN_GetNumStoredMessages>(infoQueue, 8);
            uint count = fnGetCount(infoQueue);
            if (count == 0)
            {
                Console.Error.WriteLine("[D3D11-DEBUG] 调试层已启用，但无存储消息");
                return;
            }
            var fnGetMessage = ComVTable.GetAbsolute<PFN_GetMessage>(infoQueue, 5);
            Console.Error.WriteLine($"[D3D11-DEBUG] 共 {count} 条存储消息（仅显示前 30 条）：");
            for (uint i = 0; i < count && i < 30; i++)
            {
                ulong size = 0;
                fnGetMessage(infoQueue, i, IntPtr.Zero, ref size);
                if (size == 0) continue;
                byte[] buf = new byte[size];
                fixed (byte* pBuf = buf)
                {
                    fnGetMessage(infoQueue, i, (IntPtr)pBuf, ref size);
                    // D3D11_MESSAGE x64 布局：Category(4)+Severity(4)+ID(4)+pad(4)+pDescription(8)@16+DescLen(8)@24
                    if (size >= 24)
                    {
                        long descPtrLong = BitConverter.ToInt64(buf, 16);
                        if (descPtrLong != 0)
                        {
                            string? desc = Marshal.PtrToStringAnsi((IntPtr)descPtrLong);
                            if (!string.IsNullOrEmpty(desc))
                                Console.Error.WriteLine($"  [{i}] {desc}");
                        }
                    }
                }
            }
        }
        finally
        {
            Release(infoQueue);
        }
    }

    /// <summary>平面 D3D11CreateDevice：与 FFmpegVideoDecoder 的 D3D11VA 路径同构（Hardware + BgraSupport）。</summary>
    /// <remarks>返回的 device/context 引用计数已为 1，调用方须显式 <see cref="Release"/>。</remarks>
    public static void D3D11CreateDevice(out IntPtr device, out IntPtr context)
    {
        int hr = D3D11CreateDeviceNative(
            IntPtr.Zero, D3D11_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out device, IntPtr.Zero, out context);
        if (hr < 0)
            throw new COMException($"D3D11CreateDevice 失败 (0x{hr:X8})", hr);
    }

}
