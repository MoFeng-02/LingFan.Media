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

    /// <summary>
    /// 校准 device/context 的关键 vtable 槽位：DLL 归属（VirtualQuery）+ 良性调用烟测。
    /// 任一失败抛 <see cref="InvalidOperationException"/>（loud、可诊断）。
    /// </summary>
    /// <param name="devicePtr">有效 D3D11 设备指针（共享或本次创建均可）。</param>
    /// <param name="contextPtr">有效 D3D11 设备上下文指针。</param>
    public static void VerifyVtableLayout(IntPtr devicePtr, IntPtr contextPtr)
    {
        if (devicePtr == IntPtr.Zero || contextPtr == IntPtr.Zero)
            throw new ArgumentException("校准需要有效的 device/context 指针", nameof(devicePtr));

        IntPtr d3d11Base = GetModuleHandle("d3d11.dll");
        IntPtr dxgiBase = GetModuleHandle("dxgi.dll");
        if (d3d11Base == IntPtr.Zero || dxgiBase == IntPtr.Zero)
            throw new InvalidOperationException("d3d11.dll / dxgi.dll 未加载，无法校准 vtable");

        CheckSlotModule(devicePtr, 5, d3d11Base, "ID3D11Device.CreateTexture2D");
        CheckSlotModule(contextPtr, 111, d3d11Base, "ID3D11DeviceContext.Flush");
        CheckSlotModule(contextPtr, 46, d3d11Base, "ID3D11DeviceContext.CopySubresourceRegion");

        IntPtr videoDevicePtr = QueryInterface(devicePtr, IID_ID3D11VideoDevice);
        try
        {
            CheckSlotModule(videoDevicePtr, 4, d3d11Base, "ID3D11VideoDevice.CreateVideoProcessor");
            CheckSlotModule(videoDevicePtr, 10, d3d11Base, "ID3D11VideoDevice.CreateVideoProcessorEnumerator");
            CheckSlotModule(videoDevicePtr, 8, d3d11Base, "ID3D11VideoDevice.CreateVideoProcessorInputView");
            CheckSlotModule(videoDevicePtr, 9, d3d11Base, "ID3D11VideoDevice.CreateVideoProcessorOutputView");

            IntPtr videoContextPtr = QueryInterface(contextPtr, IID_ID3D11VideoContext);
            try
            {
                CheckSlotModule(videoContextPtr, 53, d3d11Base, "ID3D11VideoContext.VideoProcessorBlt");
            }
            finally
            {
                Release(videoContextPtr);
            }
        }
        finally
        {
            Release(videoDevicePtr);
        }

        // 良性烟测：捕捉「同 DLL、错函数」类槽位错误。
        SmokeTestCreateTexture2D(devicePtr);
        SmokeTestFlush(contextPtr);
        SmokeTestCopySubresourceRegion(devicePtr, contextPtr);
    }

    private static void CheckSlotModule(IntPtr comPtr, int slot, IntPtr expectedBase, string label)
    {
        IntPtr fp = ComVTable.ReadSlot(comPtr, slot);
        if (fp == IntPtr.Zero)
            throw new InvalidOperationException($"vtable 槽位 {slot} ({label}) 解析为空指针：槽位错");

        if (VirtualQuery(fp, out MemoryBasicInformation mbi, new IntPtr(Unsafe.SizeOf<MemoryBasicInformation>())) == IntPtr.Zero)
            throw new InvalidOperationException($"VirtualQuery 失败（{label}），无法校准槽位 {slot}");

        if (mbi.AllocationBase != expectedBase)
            throw new InvalidOperationException(
                $"vtable 槽位 {slot} ({label}) 解析越界：函数属于模块 {mbi.AllocationBase} 而非预期 {expectedBase}（槽位错/野指针）");
    }

    private static void SmokeTestCreateTexture2D(IntPtr devicePtr)
    {
        var desc = new D3D11Texture2DDesc
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = 0,
            BindFlags = BindRenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        IntPtr tex;
        try
        {
            tex = CreateTexture2D(devicePtr, desc);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("vtable 槽位 5 (CreateTexture2D) 烟测失败：槽位错或野指针", ex);
        }

        if (tex == IntPtr.Zero)
            throw new InvalidOperationException("vtable 槽位 5 (CreateTexture2D) 返回 null：槽位错");

        Release(tex);
    }

    private static void SmokeTestFlush(IntPtr contextPtr)
    {
        try
        {
            Flush(contextPtr);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("vtable 槽位 111 (Flush) 烟测失败：槽位错或野指针", ex);
        }
    }

    private static void SmokeTestCopySubresourceRegion(IntPtr devicePtr, IntPtr contextPtr)
    {
        // 1×1 单平面纹理：把 src 拷到 dst，验证槽位 46 解析正确（错槽位会调用无关方法 → AV/异常）。
        // 目的仅校验 vtable 槽位，不关心像素内容。
        var desc = new D3D11Texture2DDesc
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = 0,
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        IntPtr src = IntPtr.Zero;
        IntPtr dst = IntPtr.Zero;
        try
        {
            src = CreateTexture2D(devicePtr, desc);
            dst = CreateTexture2D(devicePtr, desc);
            try
            {
                CopySubresourceRegion(contextPtr, dst, 0, 0, 0, 0, src, 0, IntPtr.Zero);
                Flush(contextPtr);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("vtable 槽位 46 (CopySubresourceRegion) 烟测失败：槽位错或野指针", ex);
            }
        }
        finally
        {
            if (dst != IntPtr.Zero)
                Release(dst);
            if (src != IntPtr.Zero)
                Release(src);
        }
    }
}


// 结构体字节布局（x64，LayoutKind.Sequential，Pack = 8）


/// <summary>DXGI_RATIONAL（8 字节）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct DxgiRational
{
    /// <summary>分子。</summary>
    public uint Numerator;

    /// <summary>分母。</summary>
    public uint Denominator;
}

/// <summary>DXGI_SAMPLE_DESC（8 字节）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct DxgiSampleDesc
{
    /// <summary>每像素样本数。</summary>
    public uint Count;

    /// <summary>采样质量。</summary>
    public uint Quality;
}

/// <summary>D3D11_TEXTURE2D_DESC（44 字节）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11Texture2DDesc
{
    /// <summary>纹理宽（像素）。</summary>
    public uint Width;

    /// <summary>纹理高（像素）。</summary>
    public uint Height;

    /// <summary>mip 层级数。</summary>
    public uint MipLevels;

    /// <summary>数组大小。</summary>
    public uint ArraySize;

    /// <summary>DXGI_FORMAT。</summary>
    public uint Format;

    /// <summary>采样描述。</summary>
    public DxgiSampleDesc SampleDesc;

    /// <summary>资源用法（D3D11_USAGE）。</summary>
    public uint Usage;

    /// <summary>绑定标志。</summary>
    public uint BindFlags;

    /// <summary>CPU 访问标志。</summary>
    public uint CPUAccessFlags;

    /// <summary>杂项标志（MiscFlags）。</summary>
    public uint MiscFlags;
}

/// <summary>D3D11_VIDEO_PROCESSOR_CONTENT_DESCRIPTION（40 字节）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11VideoProcessorContentDescription
{
    /// <summary>输入帧格式（D3D11_VIDEO_FRAME_FORMAT）。</summary>
    public uint InputFrameFormat;

    /// <summary>输入帧率。</summary>
    public DxgiRational InputFrameRate;

    /// <summary>输入宽。</summary>
    public uint InputWidth;

    /// <summary>输入高。</summary>
    public uint InputHeight;

    /// <summary>输出帧率。</summary>
    public DxgiRational OutputFrameRate;

    /// <summary>输出宽。</summary>
    public uint OutputWidth;

    /// <summary>输出高。</summary>
    public uint OutputHeight;

    /// <summary>用途（D3D11_VIDEO_USAGE）。</summary>
    public uint Usage;
}

/// <summary>
/// D3D11_VIDEO_PROCESSOR_CAPS（36 字节，d3d11.h @10412 权威：9 个 UINT）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11VideoProcessorCaps
{
    /// <summary>设备能力标志。</summary>
    public uint DeviceCaps;

    /// <summary>特性能力标志。</summary>
    public uint FeatureCaps;

    /// <summary>滤镜能力标志。</summary>
    public uint FilterCaps;

    /// <summary>输入格式能力标志。</summary>
    public uint InputFormatCaps;

    /// <summary>自动流能力标志。</summary>
    public uint AutoStreamCaps;

    /// <summary>立体能力标志。</summary>
    public uint StereoCaps;

    /// <summary>速率转换能力数量（CreateVideoProcessor 的 index 取值范围 [0, N)）。</summary>
    public uint RateConversionCapsCount;

    /// <summary>最大输入流数。</summary>
    public uint MaxInputStreams;

    /// <summary>最大流状态数。</summary>
    public uint MaxStreamStates;
}

/// <summary>
/// D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC（16 字节，d3d11.h 权威：FourCC@0 + ViewDimension@4 + union@8）。
/// <para>D3D11_VPIV_DIMENSION 仅有 UNKNOWN=0 / TEXTURE_2D=1 两值——<b>无 TEXTURE2DARRAY</b>（那是 D3D12 概念）。
/// 对 Texture2DArray 资源选片：ViewDimension=TEXTURE_2D(1) + ArraySlice=切片号
/// （ffmpeg hwcontext_d3d11va 自身即此用法）。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11VideoProcessorInputViewDesc
{
    /// <summary>FourCC（0 = 不使用）。</summary>
    public uint FourCC;

    /// <summary>视图维度（D3D11_VPIV_DIMENSION，恒 TEXTURE_2D = 1）。</summary>
    public uint ViewDimension;

    /// <summary>union.Texture2D.MipSlice。</summary>
    public uint MipSlice;

    /// <summary>union.Texture2D.ArraySlice（ffmpeg 硬解纹理即 data[1] 的切片索引）。</summary>
    public uint ArraySlice;
}

/// <summary>
/// D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC（16 字节，d3d11.h 权威：ViewDimension@0 + union@4）。
/// 与 INPUT_VIEW_DESC 不同，本结构<b>没有 FourCC 字段</b>（照抄 Input 会错位 → E_INVALIDARG）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11VideoProcessorOutputViewDesc
{
    /// <summary>视图维度（D3D11_VPOV_DIMENSION，TEXTURE2D = 1）。</summary>
    public uint ViewDimension;

    /// <summary>union.Texture2D.MipSlice（Texture2D 维度时使用）。</summary>
    public uint MipSlice;

    /// <summary>union.Texture2DArray.FirstArraySlice（Texture2D 维度时未用，恒 0）。</summary>
    public uint FirstArraySlice;

    /// <summary>union.Texture2DArray.ArraySize（Texture2D 维度时未用，恒 0）。</summary>
    public uint ArraySize;
}

/// <summary>D3D11_VIDEO_PROCESSOR_STREAM（72 字节，x64：9 真实字段 + 2 保留字段防运行时读越界）。</summary>
/// <remarks>
/// Win8.1+ 运行时在此结构体末尾读取 <c>pInputSurfaceRight</c> 字段（立体声右眼输入）。
/// 若结构体缺此字段（仅 48 字节），运行时读到栈垃圾当作 pInputSurfaceRight → 非零 →
/// VideoProcessorBlt 返回 E_INVALIDARG（调试层报 <c>VIDEOPROCESSORBLT_RIGHTNOTEXPECTED</c>）。
/// 修复：加 pInputSurfaceRight（真实字段）+ ppPastFramesRight/ppFutureFramesRight（保留字段）全部恒 Zero。
/// Win8.1 头文件未公开扩展字段，但 DDI (D3D11_1DDI_VIDEO_PROCESSOR_STREAM) 含 pInputSurfaceRight，
/// 运行时按 ≥56 字节读取。结构体扩至 72 字节确保任意读法都是零（多传无害，少传读垃圾致命）。
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11VideoProcessorStream
{
    /// <summary>是否启用（BOOL = int，1 = 启用）。</summary>
    public int Enable;

    /// <summary>输出帧索引。</summary>
    public uint OutputIndex;

    /// <summary>输入帧或场索引。</summary>
    public uint InputFrameOrField;

    /// <summary>过去帧数。</summary>
    public uint PastFrames;

    /// <summary>未来帧数。</summary>
    public uint FutureFrames;

    /// <summary>过去帧表面指针数组。</summary>
    public IntPtr ppPastFrames;

    /// <summary>输入表面指针（输入视图）。</summary>
    public IntPtr pInputSurface;

    /// <summary>未来帧表面指针数组。</summary>
    public IntPtr ppFutureFrames;

    /// <summary>立体声右眼输入表面（Win8.1+ 扩展；非立体声恒 Zero）。</summary>
    public IntPtr pInputSurfaceRight;

    /// <summary>保留（立体声扩展候选；恒 Zero）。</summary>
    public IntPtr ppPastFramesRight;

    /// <summary>保留（立体声扩展候选；恒 Zero）。</summary>
    public IntPtr ppFutureFramesRight;
}

/// <summary>
/// D3D11_VIDEO_PROCESSOR_COLOR_SPACE（4 字节，位域打包进单个 UINT）。
/// <para>位定义：bit0=Usage(0=播放/1=处理)、bit1=RGB_Range(0=全范围/1=受限)、bit2=YCbCr_Matrix(0=BT.601/1=BT.709)、
/// bit3-4=Nominal_Range(0=未定义/1=16-235/2=0-255)、bit5-31=保留。</para>
/// <para>典型取值：NV12 输入(BT.709 演播室范围) = Usage=0|RGB_Range=0|Matrix=1|Nominal=1 → Value=0x0C；
/// BGRA 输出(全范围 RGB) = Usage=0|RGB_Range=0|Matrix=0|Nominal=2 → Value=0x10。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11VideoProcessorColorSpace
{
    /// <summary>位域打包值（按上述位定义手工构造）。</summary>
    public uint Value;
}

/// <summary>RECT（16 字节：left/top/right/bottom 各 4 字节 LONG）。用于 VideoProcessorSetStreamSourceRect 等矩形状态。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct D3D11Rect
{
    /// <summary>左（含）。</summary>
    public int Left;

    /// <summary>上（含）。</summary>
    public int Top;

    /// <summary>右（不含）。</summary>
    public int Right;

    /// <summary>下（不含）。</summary>
    public int Bottom;
}

/// <summary>LUID（8 字节：LowPart uint + HighPart int）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct Luid
{
    /// <summary>低 32 位。</summary>
    public uint LowPart;

    /// <summary>高 32 位（带符号）。</summary>
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct MemoryBasicInformation
{
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public IntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}
