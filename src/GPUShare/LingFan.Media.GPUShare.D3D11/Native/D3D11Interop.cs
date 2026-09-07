using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// D3D11 手写原生 vtable P/Invoke 底座（仓级互操作事实源）。
/// </summary>
/// <remarks>
/// <para>本类以<b>全手写</b>方式解析 D3D11 / DXGI 的 COM vtable：零 Vortice、零 SharpGen 反射、
/// 零 <c>[ComImport]</c>，是 NativeAOT 100% 友好的唯一 D3D11 互操作来源。</para>
/// <para>只引用 <c>Abstractions</c> 与原生 <c>d3d11.dll</c> / <c>dxgi.dll</c>，可被仓内任意模块
/// （解码后端、渲染器、UI 合成）被动引用而无需各自重写 COM vtable —— 写一次，多层引用。</para>
/// <para>槽位与 IID 的权威值见 <c>.memory/模块注释/GPUShare.D3D11.槽位核对清单.md</c>。
/// 宪法铁律：vtable 槽位不可手算、须运行时校准；手写 IID 逐字节核对；COM vtable 委托
/// 调用约定<b>必须</b> <see cref="CallingConvention.Winapi"/>（StdCall），绝不可用 ThisCall；
/// 坏指针绝不 Release。</para>
/// </remarks>
public static unsafe partial class D3D11Interop
{
    // ─────────────────────────────────────────────────────────────────────────
    // IID（逐字节核对，勿改）
    // ─────────────────────────────────────────────────────────────────────────
    public static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static readonly Guid IID_ID3D11DeviceContext = new("c0bfa96c-e089-44fb-8eaf-26f8796190da");
    public static readonly Guid IID_ID3D11DeviceChild = new("1841e5c8-16b0-489b-bcc8-44cfb0d5deae");
    public static readonly Guid IID_ID3D11Resource = new("dc8e63f3-d12b-4952-b47b-5e45026a862d");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D11VideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
    public static readonly Guid IID_ID3D11VideoContext = new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4");
    public static readonly Guid IID_IDXGIObject = new("aec22fb8-76f3-4639-9be0-28eb43a67a2e");
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    public static readonly Guid IID_IDXGIResource1 = new("30961379-4609-4a41-998e-54fe567ee0c1");
    public static readonly Guid IID_IDXGIKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");

    /// <summary>ID3D11InfoQueue 的 IID（调试层消息队列）。</summary>
    public static readonly Guid IID_ID3D11InfoQueue = new("1f9f3a8a-6d32-4ed9-9ab5-3423d4e0c1e7");

    // ─────────────────────────────────────────────────────────────────────────
    // 常量
    // ─────────────────────────────────────────────────────────────────────────
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
    /// 原因：① NT 句柄共享无需 keyed mutex（keyed mutex 是 legacy MISC_SHARED 路径的同步原语）；
    /// ② keyed mutex 与 NV12 视频格式组合会使 CreateSharedHandle 返回 DXGI_ERROR_INVALID_CALL。
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

    // ─────────────────────────────────────────────────────────────────────────
    // 嵌套 vtable 读取器（绝对 0 基槽位）
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// COM vtable 读取器：<c>comPtr → vtable 指针 → [slot * IntPtr.Size] = 方法指针</c>。
    /// <paramref name="slot"/> 一律为绝对 0 基槽位（0=QueryInterface, 1=AddRef, 2=Release）。
    /// </summary>
    public static class ComVTable
    {
        /// <summary>读取指定绝对槽位的方法指针（不构造委托）。</summary>
        public static IntPtr ReadSlot(IntPtr comPtr, int slot)
        {
            IntPtr vtable = Marshal.ReadIntPtr(comPtr);
            return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        }

        /// <summary>读取指定绝对槽位并构造强类型委托。</summary>
        public static TDelegate Get<TDelegate>(IntPtr comPtr, int slot) where TDelegate : Delegate
        {
            IntPtr fp = ReadSlot(comPtr, slot);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(fp);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // vtable 委托原型（CallingConvention.Winapi = StdCall，绝不用 ThisCall）
    // ─────────────────────────────────────────────────────────────────────────
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    /// <summary>
    /// IDXGIResource1::CreateSharedHandle（绝对槽位 13）。
    /// 原生签名：<c>HRESULT CreateSharedHandle(pAttributes, dwAccess, lpName, pHandle)</c>——4 参 + This = 5 总参。
    /// </summary>
    private delegate int PFN_CreateSharedHandle(IntPtr self, IntPtr pAttributes, uint dwAccess, IntPtr lpName, out IntPtr pHandle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_AcquireSync(IntPtr self, ulong key, uint dwMilliseconds);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_ReleaseSync(IntPtr self, ulong key);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetAdapter(IntPtr self, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_GetDesc1(IntPtr self, IntPtr pDesc);

    // ─────────────────────────────────────────────────────────────────────────
    // 平面导出函数（非 vtable）
    // ─────────────────────────────────────────────────────────────────────────
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

    // ─────────────────────────────────────────────────────────────────────────
    // 公开互操作原语
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>QueryInterface：失败抛 <see cref="COMException"/>。</summary>
    public static IntPtr QueryInterface(IntPtr comPtr, Guid iid)
    {
        var fn = ComVTable.Get<PFN_QueryInterface>(comPtr, 0);
        int hr = fn(comPtr, ref iid, out IntPtr ppv);
        if (hr < 0)
            throw new COMException($"QueryInterface({iid:B}) 失败 (0x{hr:X8})", hr);
        return ppv;
    }

    /// <summary>QueryInterface：失败返回 <c>false</c>（不抛）。</summary>
    public static bool TryQueryInterface(IntPtr comPtr, Guid iid, out IntPtr ppv)
    {
        var fn = ComVTable.Get<PFN_QueryInterface>(comPtr, 0);
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
            var fn = ComVTable.Get<PFN_GetDevice>(child, 3);
            fn(child, out IntPtr device);
            return device;
        }
        finally
        {
            Release(child);
        }
    }

    /// <summary>AddRef（返回新引用计数）。</summary>
    public static int AddRef(IntPtr comPtr) => ComVTable.Get<PFN_AddRefRelease>(comPtr, 1)(comPtr);

    /// <summary>
    /// 经 ID3D11Texture2D::GetDesc（绝对槽位 10）读取纹理 DESC（只读，不改引用计数、无副作用）。
    /// 用于诊断解码侧硬解纹理的真实维度（ArraySize / BindFlags / Format / MiscFlags）。
    /// </summary>
    public static D3D11Texture2DDesc GetTexture2DDesc(IntPtr texture2DPtr)
    {
        if (texture2DPtr == IntPtr.Zero)
            throw new ArgumentException("texture2D 句柄不可为 Zero", nameof(texture2DPtr));
        var fn = ComVTable.Get<PFN_GetTexture2DDesc>(texture2DPtr, 10);
        fn(texture2DPtr, out var desc);
        return desc;
    }



    /// <summary>Release（返回新引用计数；本模块全程仅持 IntPtr，显式 Release 规避双重释放）。</summary>
    public static int Release(IntPtr comPtr) => ComVTable.Get<PFN_AddRefRelease>(comPtr, 2)(comPtr);

    /// <summary>ID3D11Device::CreateTexture2D（绝对槽位 5）。</summary>
    public static IntPtr CreateTexture2D(IntPtr devicePtr, in D3D11Texture2DDesc desc)
    {
        // 栈上局部变量取址（不经 fixed 语句）：跨标准 Roslyn / 本机编译器均合法，
        // 栈帧在方法返回前不被 GC 移动，与 fixed 语义等价。
        D3D11Texture2DDesc d = desc;
        D3D11Texture2DDesc* p = &d;
        var fn = ComVTable.Get<PFN_CreateTexture2D>(devicePtr, 5);
        int hr = fn(devicePtr, (IntPtr)p, IntPtr.Zero, out IntPtr tex);
        if (hr < 0)
            throw new COMException($"CreateTexture2D 失败 (0x{hr:X8})", hr);
        return tex;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorEnumerator（绝对槽位 10）。</summary>
    public static IntPtr CreateVideoProcessorEnumerator(IntPtr videoDevicePtr, in D3D11VideoProcessorContentDescription desc)
    {
        D3D11VideoProcessorContentDescription d = desc;
        D3D11VideoProcessorContentDescription* p = &d;
        var fn = ComVTable.Get<PFN_CreateVideoProcessorEnumerator>(videoDevicePtr, 10);
        int hr = fn(videoDevicePtr, (IntPtr)p, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"CreateVideoProcessorEnumerator 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessor（绝对槽位 4）。</summary>
    public static IntPtr CreateVideoProcessor(IntPtr videoDevicePtr, IntPtr enumeratorPtr, uint contentDescIndex)
    {
        var fn = ComVTable.Get<PFN_CreateVideoProcessor>(videoDevicePtr, 4);
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
        var fn = ComVTable.Get<PFN_GetVideoProcessorCaps>(enumeratorPtr, 9);
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
        var fn = ComVTable.Get<PFN_CheckVideoProcessorFormat>(enumeratorPtr, 8);
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
        var fn = ComVTable.Get<PFN_GetVideoProcessorContentDesc>(enumeratorPtr, 7);
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
        var fn = ComVTable.Get<PFN_CreateVideoProcessorInputView>(videoDevicePtr, 8);
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
        var fn = ComVTable.Get<PFN_CreateVideoProcessorOutputView>(videoDevicePtr, 9);
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
        var fn = ComVTable.Get<PFN_VideoProcessorBlt>(videoContextPtr, 53);
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
        var fn = ComVTable.Get<PFN_VideoProcessorSetStreamFrameFormat>(videoContextPtr, 27);
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
        var fn = ComVTable.Get<PFN_VideoProcessorSetStreamColorSpace>(videoContextPtr, 28);
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
        var fn = ComVTable.Get<PFN_VideoProcessorSetOutputColorSpace>(videoContextPtr, 15);
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
        var fn = ComVTable.Get<PFN_VideoProcessorSetStreamSourceRect>(videoContextPtr, 30);
        fn(videoContextPtr, processor, streamIndex, enable, (IntPtr)p);
    }

    /// <summary>ID3D11DeviceContext::Flush（绝对槽位 111）。</summary>
    public static void Flush(IntPtr contextPtr)
    {
        var fn = ComVTable.Get<PFN_Flush>(contextPtr, 111);
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
        var fn = ComVTable.Get<PFN_CopySubresourceRegion>(contextPtr, 46);
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
            var fnGetCount = ComVTable.Get<PFN_GetNumStoredMessages>(infoQueue, 8);
            uint count = fnGetCount(infoQueue);
            if (count == 0)
            {
                Console.Error.WriteLine("[D3D11-DEBUG] 调试层已启用，但无存储消息");
                return;
            }
            var fnGetMessage = ComVTable.Get<PFN_GetMessage>(infoQueue, 5);
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

    /// <summary>IDXGIResource1::CreateSharedHandle（绝对槽位 13）；pAttributes=null、lpName=null、dwAccess=access。</summary>
    public static IntPtr CreateSharedHandle(IntPtr dxgiResource1Ptr, uint access)
    {
        var fn = ComVTable.Get<PFN_CreateSharedHandle>(dxgiResource1Ptr, 13);
        int hr = fn(dxgiResource1Ptr, IntPtr.Zero, access, IntPtr.Zero, out IntPtr handle);
        if (hr < 0)
            throw new COMException($"CreateSharedHandle 失败 (0x{hr:X8})", hr);
        return handle;
    }

    /// <summary>IDXGIKeyedMutex::AcquireSync（绝对槽位 8）。</summary>
    /// <remarks>返回 <c>S_OK</c> 才视为成功；<c>WAIT_TIMEOUT</c>（0x102，正数）表示超时未获锁，
    /// 必须抛异常让调用方回落，绝不可继续写入共享纹理（竞态）。</remarks>
    public static void AcquireSync(IntPtr keyedMutexPtr, ulong key, uint milliseconds)
    {
        var fn = ComVTable.Get<PFN_AcquireSync>(keyedMutexPtr, 8);
        int hr = fn(keyedMutexPtr, key, milliseconds);
        if (hr != 0)
            throw new COMException($"AcquireSync 失败/超时 (0x{hr:X8})", hr);
    }

    /// <summary>IDXGIKeyedMutex::ReleaseSync（绝对槽位 9）。</summary>
    public static void ReleaseSync(IntPtr keyedMutexPtr, ulong key)
    {
        var fn = ComVTable.Get<PFN_ReleaseSync>(keyedMutexPtr, 9);
        int hr = fn(keyedMutexPtr, key);
        if (hr != 0)
            throw new COMException($"ReleaseSync 失败 (0x{hr:X8})", hr);
    }

    /// <summary>IDXGIDevice::GetAdapter（绝对槽位 7）。</summary>
    public static IntPtr GetAdapter(IntPtr dxgiDevicePtr)
    {
        var fn = ComVTable.Get<PFN_GetAdapter>(dxgiDevicePtr, 7);
        int hr = fn(dxgiDevicePtr, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"GetAdapter 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>
    /// 查询设备所属 GPU 适配器的 vendor+描述（设备→IDXGIDevice(QI)→GetAdapter(槽7)→GetDesc1(槽10)）。
    /// 用于铁证「设备实际落在哪张 GPU」（2026-08-19：疑 D3D11CreateDeviceOnAdapter 设备实际在核显）。
    /// </summary>
    public static (uint Vendor, string Description) GetDeviceAdapterInfo(IntPtr devicePtr)
    {
        IntPtr dxgiDev = IntPtr.Zero;
        try
        {
            if (!TryQueryInterface(devicePtr, IID_IDXGIDevice, out dxgiDev))
                return (0xFFFFFFFF, "(QI IDXGIDevice 失败)");
            IntPtr adapter = GetAdapter(dxgiDev);
            try
            {
                var info = GetAdapterDesc1Info(adapter);
                return (info.Vendor, info.Description);
            }
            finally
            {
                Release(adapter);
            }
        }
        finally
        {
            if (dxgiDev != IntPtr.Zero)
                Release(dxgiDev);
        }
    }

    /// <summary>IDXGIAdapter1::GetDesc1（绝对槽位 10）：仅读取 DXGI_ADAPTER_DESC1 的 LUID（@296，8 字节小端）。</summary>
    public static void GetDesc1Luid(IntPtr adapter1Ptr, out uint luidLow, out int luidHigh)
    {
        // DXGI_ADAPTER_DESC1 = 308 字节，LUID 在 @296（LowPart uint@296, HighPart int@300）。
        Span<byte> buf = stackalloc byte[308];
        fixed (byte* p = buf)
        {
            var fn = ComVTable.Get<PFN_GetDesc1>(adapter1Ptr, 10);
            int hr = fn(adapter1Ptr, (IntPtr)p);
            if (hr < 0)
                throw new COMException($"GetDesc1 失败 (0x{hr:X8})", hr);
        }

        luidLow = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(296));
        luidHigh = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(300));
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
    /// 在指定适配器（pAdapter）上创建 D3D11 设备（DriverType=UNKNOWN + BgraSupport|VideoSupport）。
    /// 用于把 D3D11VA/VideoProcessor 绑定到首选适配器（独显优先，见 <see cref="FindPreferredAdapter"/>）。
    /// </summary>
    public static void D3D11CreateDeviceOnAdapter(IntPtr adapterPtr, out IntPtr device, out IntPtr context)
    {
        // 不启用 D3D11_CREATE_DEVICE_DEBUG：部分激活的调试层（vtable 被包装但 InfoQueue 不可用）
        // 会让 IDXGIResource1::CreateSharedHandle 返回 DXGI_ERROR_INVALID_CALL，破坏 BGRA 共享句柄导出。
        // 调试层仅作诊断用，生产路径不启用。
        uint flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        int hr = D3D11CreateDeviceNative(
            adapterPtr, D3D11_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, flags,
            IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out device, IntPtr.Zero, out context);
        if (hr < 0)
            throw new COMException($"D3D11CreateDevice(OnAdapter) 失败 (0x{hr:X8})", hr);
    }

    /// <summary>
    /// 枚举全部 DXGI 适配器，选择「独显优先」的首选适配器。
    /// 策略：跳过软件适配器（Microsoft Basic Render Driver），取 DedicatedVideoMemory 最大者
    /// （独显 &gt;&gt; 集显；无独显时退化为显存最大的集显）。不绑定任何厂商/型号（2026-08-20 泛化）。
    /// 调用方负责 <see cref="Release"/>；无可用适配器返回 <see cref="IntPtr.Zero"/>（调用方回退默认路径）。
    /// </summary>
    public static IntPtr FindPreferredAdapter()
    {
        Guid iid = IID_IDXGIFactory1;
        int hr = CreateDXGIFactory1Native(ref iid, out IntPtr factory);
        if (hr < 0 || factory == IntPtr.Zero)
            throw new COMException($"CreateDXGIFactory1 失败 (0x{hr:X8})", hr);
        try
        {
            IntPtr best = IntPtr.Zero;
            ulong bestMemory = 0;
            for (uint i = 0; ; i++)
            {
                int enumHr = EnumAdapters1(factory, i, out IntPtr adapter);
                if (enumHr == DXGI_ERROR_NOT_FOUND)
                    break; // 没有更多适配器
                if (enumHr < 0)
                    throw new COMException($"EnumAdapters1({i}) 失败 (0x{enumHr:X8})", enumHr);
                IntPtr candidate = adapter;
                try
                {
                    var info = GetAdapterDesc1Info(adapter);
                    if ((info.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                        continue; // 软件适配器（Basic Render Driver）不可用，跳过
                    if (info.DedicatedVideoMemory > bestMemory)
                    {
                        // 替换更优候选：旧 best 释放，adapter 所有权转移给 best（finally 不释放）。
                        if (best != IntPtr.Zero)
                            Release(best);
                        best = adapter;
                        bestMemory = info.DedicatedVideoMemory;
                        candidate = IntPtr.Zero;
                    }
                }
                finally
                {
                    if (candidate != IntPtr.Zero)
                        Release(candidate);
                }
            }
            return best;
        }
        finally
        {
            Release(factory);
        }
    }

    /// <summary>IDXGIFactory1::EnumAdapters1（绝对槽位 12）。返回 HRESULT（DXGI_ERROR_NOT_FOUND=无更多）。</summary>
    private static int EnumAdapters1(IntPtr factoryPtr, uint adapterIndex, out IntPtr ppAdapter)
    {
        var fn = ComVTable.Get<PFN_EnumAdapters1>(factoryPtr, 12);
        return fn(factoryPtr, adapterIndex, out ppAdapter);
    }

    /// <summary>
    /// 读 DXGI_ADAPTER_DESC1 关键字段——GetDesc1 绝对槽位 10。
    /// 布局（308 字节）：Description @0（WCHAR[128]，256B）→ VendorId @256（UINT）→ DeviceId @260 →
    /// SubSysId @264 → Revision @268 → DedicatedVideoMemory @272（SIZE_T，x64 8B）→
    /// DedicatedSystemMemory @280 → SharedSystemMemory @288 → AdapterLuid @296（8B）→ Flags @304（UINT）。
    /// 注：VendorId 不在 @0——@0 是 WCHAR[128] 描述（2026-08-19 实锤修正）。
    /// </summary>
    private static (uint Vendor, string Description, ulong DedicatedVideoMemory, uint Flags) GetAdapterDesc1Info(IntPtr adapterPtr)
    {
        Span<byte> buf = stackalloc byte[308];
        fixed (byte* p = buf)
        {
            var fn = ComVTable.Get<PFN_GetDesc1>(adapterPtr, 10);
            int hr = fn(adapterPtr, (IntPtr)p);
            if (hr < 0)
                throw new COMException($"GetDesc1 失败 (0x{hr:X8})", hr);
        }
        uint vendor = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(256));
        ulong dedicatedMemory = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(272));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(304));
        // Description @0：WCHAR[128]（UTF-16LE），遇 \0 截断。
        int len = 0;
        while (len < 128 && BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(len * 2)) != 0)
            len++;
        string desc = System.Text.Encoding.Unicode.GetString(buf.Slice(0, len * 2));
        return (vendor, desc, dedicatedMemory, flags);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 运行时扫描校准（槽位错 → 立即可诊断失败，绝不带野指针继续调）
    // ─────────────────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────────────────
// 结构体字节布局（x64，LayoutKind.Sequential，Pack = 8）
// ─────────────────────────────────────────────────────────────────────────

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
