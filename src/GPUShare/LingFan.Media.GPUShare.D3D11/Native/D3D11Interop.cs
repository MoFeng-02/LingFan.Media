using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Interop;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// D3D11 手写原生 vtable P/Invoke 底座（仓级互操作事实源）。
/// </summary>
/// <remarks>
/// <para>本类以<b>全手写</b>方式解析 D3D11 / DXGI 的 COM vtable：零 Vortice、零 SharpGen 反射、
/// 零 <c>[ComImport]</c>，是 NativeAOT 100% 友好的唯一 D3D11 互操作来源。</para>
/// <para>只引用 <c>Abstractions</c> 与原生 <c>d3d11.dll</c> / <c>dxgi.dll</c>，可被仓内任意模块
/// （解码后端、渲染器、UI 合成）被动引用而无需各自重写 COM vtable —— 写一次，多层引用。</para>
/// <para>槽位与 IID 的核对规则：vtable 槽位不可手算、须运行时校准；手写 IID 逐字节核对；COM vtable 委托
/// 调用约定<b>必须</b> <see cref="CallingConvention.Winapi"/>（StdCall），绝不可用 ThisCall；
/// 坏指针绝不 Release。</para>
/// </remarks>
public static unsafe partial class D3D11Interop
{

    // IID（逐字节核对，勿改）

    /// <summary>ID3D11Device 的 IID。</summary>
    public static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    /// <summary>ID3D11DeviceContext 的 IID。</summary>
    public static readonly Guid IID_ID3D11DeviceContext = new("c0bfa96c-e089-44fb-8eaf-26f8796190da");
    /// <summary>ID3D11DeviceChild 的 IID。</summary>
    public static readonly Guid IID_ID3D11DeviceChild = new("1841e5c8-16b0-489b-bcc8-44cfb0d5deae");
    /// <summary>ID3D11Resource 的 IID。</summary>
    public static readonly Guid IID_ID3D11Resource = new("dc8e63f3-d12b-4952-b47b-5e45026a862d");
    /// <summary>ID3D11Texture2D 的 IID。</summary>
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    /// <summary>ID3D11VideoDevice 的 IID。</summary>
    public static readonly Guid IID_ID3D11VideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
    /// <summary>ID3D11VideoContext 的 IID。</summary>
    public static readonly Guid IID_ID3D11VideoContext = new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4");
    /// <summary>IDXGIObject 的 IID。</summary>
    public static readonly Guid IID_IDXGIObject = new("aec22fb8-76f3-4639-9be0-28eb43a67a2e");
    /// <summary>IDXGIDevice 的 IID。</summary>
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    /// <summary>IDXGIAdapter1 的 IID。</summary>
    public static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    /// <summary>IDXGIResource1 的 IID。</summary>
    public static readonly Guid IID_IDXGIResource1 = new("30961379-4609-4a41-998e-54fe567ee0c1");
    /// <summary>IDXGIKeyedMutex 的 IID。</summary>
    public static readonly Guid IID_IDXGIKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");

    /// <summary>ID3D11InfoQueue 的 IID（调试层消息队列）。</summary>
    public static readonly Guid IID_ID3D11InfoQueue = new("1f9f3a8a-6d32-4ed9-9ab5-3423d4e0c1e7");


    // 常量

    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;
    private const uint D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX = 0x2;
    private const uint D3D11_RESOURCE_MISC_SHARED_NTHANDLE = 0x800;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
    private const uint D3D11_CREATE_DEVICE_DEBUG = 0x2;
    private const int D3D11_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D11_DRIVER_TYPE_UNKNOWN = 0;   // pAdapter 非空时必须 UNKNOWN（D3D_DRIVER_TYPE：UNKNOWN=0, HARDWARE=1, WARP=5）
    private const uint D3D11_SDK_VERSION = 7;
    private const uint DXGI_SHARED_RESOURCE_READ = 0x80000000;
    private const uint DXGI_SHARED_RESOURCE_WRITE = 0x1;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x1;   // DXGI_ADAPTER_DESC1.Flags：软件适配器（Microsoft Basic Render Driver）

    /// <summary>IDXGIFactory1 的 IID。</summary>
    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>DXGI 共享句柄访问掩码：读 | 写。</summary>
    public const uint SharedResourceReadWrite = DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE;

    /// <summary>RGBA 输出纹理 MiscFlags：SharedKeyedMutex | SharedNTHandle（OpenSharedResource1 仅接受 NT 句柄）。</summary>
    public const uint RgbaTextureMiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    /// <summary>NV12 共享纹理 MiscFlags：仅 SharedNTHandle（不含 SharedKeyedMutex）。
    /// 原因：(1) NT 句柄共享无需 keyed mutex（keyed mutex 是 legacy MISC_SHARED 路径的同步原语）；
    /// (2) keyed mutex 与 NV12 视频格式组合会使 CreateSharedHandle 返回 DXGI_ERROR_INVALID_CALL。
    /// 该纹理仅用于 Vulkan 跨 API 经外部内存导入（Vulkan 侧不做 keyed mutex acquire）。</summary>
    public const uint Nv12TextureMiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    /// <summary>B8G8R8A8 格式值。</summary>
    public const uint FormatB8G8R8A8Unorm = DXGI_FORMAT_B8G8R8A8_UNORM;

    /// <summary>NV12 双平面格式值（DXGI_FORMAT_NV12 = 103；注意 41 是 NV11，勿混用）。</summary>
    public const uint FormatNv12 = 103;

    /// <summary>视频处理器格式支持标志：输入。</summary>
    public const uint VideoProcessorFormatInput = 0x1;

    /// <summary>视频处理器格式支持标志：输出。</summary>
    public const uint VideoProcessorFormatOutput = 0x2;

    /// <summary>RenderTarget 绑定标志。</summary>
    public const uint BindRenderTarget = D3D11_BIND_RENDER_TARGET;

    /// <summary>ShaderResource 绑定标志（NV12 等可采样但不可渲染格式；不可绑 RenderTarget 的格式用此）。</summary>
    public const uint BindShaderResource = 0x8;


    // 嵌套 vtable 读取器（绝对 0 基槽位）

    // COM vtable 槽位读取统一走 LingFan.Media.Interop.ComVTable（本工程约定：绝对 0 基槽位，0=QueryInterface）。
    // 历史教训：本地 ComVTable.Get 为绝对槽位语义，收敛共享底座后改名 GetAbsolute 以区分 WASAPI/MF 的相对槽位 Get。
    // vtable 委托原型（CallingConvention.Winapi = StdCall，绝不用 ThisCall）

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_QueryInterface(IntPtr self, ref Guid iid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_AddRefRelease(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CreateTexture2D(IntPtr self, IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTexture2D);

    /// <summary>ID3D11Texture2D::GetDesc（绝对槽位 10，ID3D11Resource 三方法之后第一项）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_GetTexture2DDesc(IntPtr self, out D3D11Texture2DDesc pDesc);

    /// <summary>ID3D11DeviceChild::GetDevice（绝对槽位 3，IUnknown 三方法之后第一项）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_GetDevice(IntPtr self, out IntPtr ppDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CreateVideoProcessor(IntPtr self, IntPtr pEnumerator, uint contentDescIndex, out IntPtr ppVideoProcessor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CreateVideoProcessorEnumerator(IntPtr self, IntPtr pContentDesc, out IntPtr ppEnum);

    /// <summary>ID3D11VideoProcessorEnumerator::GetVideoProcessorCaps（绝对槽位 9，MIDL 序权威）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetVideoProcessorCaps(IntPtr self, IntPtr pCaps);

    /// <summary>ID3D11VideoProcessorEnumerator::CheckVideoProcessorFormat（绝对槽位 8，MIDL 序权威）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CheckVideoProcessorFormat(IntPtr self, uint format, IntPtr pFlags);

    /// <summary>ID3D11VideoProcessorEnumerator::GetVideoProcessorContentDesc（绝对槽位 7，MIDL 序权威）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetVideoProcessorContentDesc(IntPtr self, IntPtr pContentDesc);

    /// <summary>IDXGIFactory1::EnumAdapters1（绝对槽位 12，MIDL 序：IUnknown3+IDXGIObject4+IDXGIFactory5+自身1）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_EnumAdapters1(IntPtr self, uint adapter, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CreateVideoProcessorInputView(IntPtr self, IntPtr pResource, IntPtr pEnumerator, IntPtr pDesc, out IntPtr ppInputView);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CreateVideoProcessorOutputView(IntPtr self, IntPtr pResource, IntPtr pEnumerator, IntPtr pDesc, out IntPtr ppOutputView);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_VideoProcessorBlt(IntPtr self, IntPtr pVideoProcessor, IntPtr pView, uint outputFrame, uint streamCount, IntPtr pStreams);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamFrameFormat（绝对槽位 27，void 返回，状态设置器）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_VideoProcessorSetStreamFrameFormat(IntPtr self, IntPtr pVideoProcessor, uint streamIndex, uint frameFormat);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamColorSpace（绝对槽位 28，void 返回，状态设置器）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_VideoProcessorSetStreamColorSpace(IntPtr self, IntPtr pVideoProcessor, uint streamIndex, IntPtr pColorSpace);

    /// <summary>ID3D11VideoContext::VideoProcessorSetOutputColorSpace（绝对槽位 15，void 返回，状态设置器）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_VideoProcessorSetOutputColorSpace(IntPtr self, IntPtr pVideoProcessor, IntPtr pColorSpace);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamSourceRect（绝对槽位 30，void 返回，状态设置器）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_VideoProcessorSetStreamSourceRect(IntPtr self, IntPtr pVideoProcessor, uint streamIndex, int enable, IntPtr pSourceRect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_Flush(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PFN_CopySubresourceRegion(IntPtr self, IntPtr pDstResource, uint dstSubresource,
        uint dstX, uint dstY, uint dstZ, IntPtr pSrcResource, uint srcSubresource, IntPtr pSrcBox);

    /// <summary>
    /// IDXGIResource1::CreateSharedHandle（绝对槽位 13）。
    /// 原生签名：<c>HRESULT CreateSharedHandle(pAttributes, dwAccess, lpName, pHandle)</c>——4 参 + This = 5 总参。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CreateSharedHandle(IntPtr self, IntPtr pAttributes, uint dwAccess, IntPtr lpName, out IntPtr pHandle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_AcquireSync(IntPtr self, ulong key, uint dwMilliseconds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_ReleaseSync(IntPtr self, ulong key);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetAdapter(IntPtr self, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetDesc1(IntPtr self, IntPtr pDesc);


    // 平面导出函数（非 vtable）

    // 调用约定默认 Winapi（StdCall），与 COM ABI 一致；此处不显式写 CallingConvention 以
    // 免引入 Vortice 移除后不再传递引用的 System.Runtime.InteropServices 外观程序集。
    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Error)]
    private static partial int D3D11CreateDeviceNative(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, out IntPtr ppImmediateContext);

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Error)]
    private static partial int CreateDXGIFactory1Native(ref Guid riid, out IntPtr ppFactory);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static partial IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualQuery", SetLastError = true)]
    private static partial IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, IntPtr dwLength);

    // 公开互操作原语按功能域拆分至 D3D11Interop.*.cs（部分类，编译期等价）。
}
