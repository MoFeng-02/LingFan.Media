using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Interop;
namespace LingFan.Media.GPUShare.D3D11;

// D3D11Interop 的 vtable 校准烟测职责段（部分类，按职责拆分，编译期等价）。
// 槽位模块归属校验（VirtualQuery AllocationBase）与良性烟测（捕捉同 DLL 错函数）；
// COM 基础原语在 Core 主段。
public static unsafe partial class D3D11Interop
{
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
