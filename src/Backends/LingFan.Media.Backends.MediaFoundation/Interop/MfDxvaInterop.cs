using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// MediaFoundation DXVA 硬件解码零拷贝互操作。
/// </summary>
/// <remarks>
/// <para>提供：① <see cref="MFCreateDXGIDeviceManager"/>（mfplat.dll 扁平导出）创建 DXGI 设备管理器；
/// ② <see cref="D3D11CreateDevice"/>（d3d11.dll 扁平导出）创建窗口无关共享 D3D11 设备（无头模式自备）；
/// ③ <c>IMFDXGIDeviceManager.ResetDevice</c> / <c>IMFDXGIBuffer.GetResource</c> / <c>IMFDXGIBuffer.GetSubresourceIndex</c>
/// 三个原始 vtable 委托（与 <see cref="MfVTable"/> 同款按槽取函数指针）。</para>
/// <para><b>AOT 兼容</b>：全 <c>[LibraryImport]</c> 源生成 P/Invoke + 原始 vtable 委托（<c>CallingConvention.Winapi</c>），无反射、无 <c>[ComImport]</c>。</para>
/// <para><b>依赖倒置</b>：本类仅暴露原生设备/纹理句柄（<see cref="IntPtr"/>），不引用任何渲染器模块；
/// 调用方（MFVideoDecoder）经 Abstractions 的 <c>IGpuDeviceContext</c> / <c>IGpuTextureResource</c> 契约与渲染器解耦。</para>
/// <para>仅 Windows 可用。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class MfDxvaInterop
{
    private const string MfplatDll = "mfplat.dll";
    private const string D3D11Dll = "d3d11.dll";

    // D3D11_CREATE_DEVICE_FLAG / D3D_DRIVER_TYPE / SDK 版本常量（d3d11.h / d3dcommon.h）
    private const uint D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800; // DXVA 硬解要求设备支持视频（SDK d3d11.h:15018 权威值）
    private const uint D3D11_SDK_VERSION = 7;

    /// <summary>创建 DXGI 设备管理器（DXVA 必需）。返回 IMFDXGIDeviceManager COM 指针 + resetToken。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr ppDeviceManager);

    /// <summary>创建窗口无关 D3D11 设备（无头模式 DXVA 自备；有头模式由渲染器经 IGpuDeviceContext 提供）。</summary>
    [LibraryImport(D3D11Dll)]
    internal static partial int D3D11CreateDevice(
        IntPtr pAdapter,
        uint DriverType,
        IntPtr Software,
        uint Flags,
        IntPtr pFeatureLevels,
        uint FeatureLevels,
        uint SDKVersion,
        out IntPtr ppDevice,
        out uint pFeatureLevel,
        out IntPtr ppImmediateContext);

    /// <summary>创建硬件 D3D11 设备（BGRA 支持）；失败抛 HResult 异常并把输出清零。</summary>
    internal static void CreateD3D11Device(out IntPtr device, out IntPtr context)
    {
        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            IntPtr.Zero,
            0,
            D3D11_SDK_VERSION,
            out device,
            out _,
            out context);
        if (hr < 0)
        {
            device = IntPtr.Zero;
            context = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    // ── IMFDXGIDeviceManager（IUnknown 之后 vtable 绝对槽，MfVTable.Get 的 slotIndex = 绝对槽 − 3）：
    //    CloseDeviceHandle=3(→0), GetVideoService=4(→1), LockDevice=5(→2), OpenDeviceHandle=6(→3),
    //    ResetDevice=7(→4), TestDevice=8(→5), UnlockDevice=9(→6) ──
    //  顺序以 SDK 头文件（IMFDXGIDeviceManagerVtbl）为权威：
    //  ResetDevice 在绝对槽 7（即 slotIndex=4）。若误置为 slotIndex=1（=GetVideoService）则语义错误，须以 SDK 头文件为准。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IMFDXGIDeviceManager_ResetDevice(IntPtr self, IntPtr pUnkDevice, uint resetToken);

    // 决定性验证：解码器实际经本管理器取设备，故须验证管理器内部真的绑定上了设备。
    //    ResetDevice 的 P/Invoke 若有偏差，HRESULT 仍可能成功，但管理器内部设备为空/错 ⇒ 解码器
    //    GetVideoService 取回空设备 ⇒ 静默读回系统内存（PROVIDES_SAMPLES 仍可为 True）。本探针直接
    //    从管理器取回 ID3D11Device 并复测 DXVA 能力，是「绑定是否真正生效」的唯一权威判据。
    //    SDK 实物 mfobjects.h:6631-6639：GetVideoService 真实签名为
    //        HRESULT GetVideoService(THIS, _In_ HANDLE hDevice, _In_ REFIID riid, _Outptr_ void** ppService)
    //      即紧接 This 之后的【第一个参数是 OpenDeviceHandle 取得的 HANDLE】，此前手写委托漏掉该参数
    //      ⇒ 调用栈错位（riid 落到了 hDevice 位、ppService 落到 riid 位、少压一个参数）
    //      ⇒ 返回 0x80070006 假失败（假阴性），会被误判 ResetDevice 未绑定。必须从 OpenDeviceHandle 取 HANDLE 再调用。
    //    OpenDeviceHandle：HRESULT OpenDeviceHandle(THIS, HANDLE* phDevice)；绝对槽 6 → slot 3。
    //    CloseDeviceHandle：HRESULT CloseDeviceHandle(THIS, HANDLE hDevice)；绝对槽 3 → slot 0。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IMFDXGIDeviceManager_OpenDeviceHandle(IntPtr self, out IntPtr phDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IMFDXGIDeviceManager_CloseDeviceHandle(IntPtr self, IntPtr hDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IMFDXGIDeviceManager_GetVideoService(IntPtr self, IntPtr hDevice, ref Guid riid, out IntPtr ppService);

    // ── IMFDXGIBuffer（IUnknown 之后：GetResource=3, GetSubresourceIndex=4）──
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IMFDXGIBuffer_GetResource(IntPtr self, ref Guid guid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IMFDXGIBuffer_GetSubresourceIndex(IntPtr self, out uint puSubresource);

    // ── ID3D10Multithread（d3d10.h ID3D10MultithreadVtbl 实物顺序）：
    //    QueryInterface=0, AddRef=1, Release=2, Enter=3, Leave=4, SetMultithreadProtected=5, GetMultithreadProtected=6。
    //    MfVTable.Get 的 slotIndex = 绝对槽 − 3 ⇒ SetMultithreadProtected = slotIndex 2。
    //    返回类型是 **BOOL（返回旧状态）而非 HRESULT**，绝不能按 HRESULT 判失败（旧状态 FALSE=0 会被误读成 S_OK，
    //    旧状态 TRUE=1 会被误读成 S_FALSE）——此处只取返回值作诊断，不做成败判定。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int ID3D10Multithread_SetMultithreadProtected(IntPtr self, int bMTProtect);

    /// <summary>
    /// 对 D3D11 设备开启多线程保护（DXVA 硬解硬性前置：解码 MFT 与渲染分处不同线程共享同一设备）。
    /// </summary>
    /// <returns>成功开启返回 true；设备不支持 <c>ID3D10Multithread</c> 时返回 false（调用方决定是否继续）。</returns>
    internal static bool TryEnableMultithreadProtection(IntPtr d3d11Device)
    {
        if (d3d11Device == IntPtr.Zero) return false;
        Guid iid = MFConstants.IID_ID3D10Multithread;
        int hr = Marshal.QueryInterface(d3d11Device, in iid, out IntPtr mt);
        if (hr < 0 || mt == IntPtr.Zero) return false;
        try
        {
            MfVTable.Get<ID3D10Multithread_SetMultithreadProtected>(mt, 2)(mt, 1);
            return true;
        }
        finally
        {
            Marshal.Release(mt);
        }
    }

    // ── ID3D11VideoDevice（d3d11.h ID3D11VideoDeviceVtbl 实物顺序，IUnknown 之后绝对槽）：
    //    CreateVideoDecoder=3, CreateVideoProcessor=4, CreateAuthenticatedChannel=5, CreateCryptoSession=6,
    //    CreateVideoDecoderOutputView=7, CreateVideoProcessorInputView=8, CreateVideoProcessorOutputView=9,
    //    CreateVideoProcessorEnumerator=10, GetVideoDecoderProfileCount=11, GetVideoDecoderProfile=12,
    //    CheckVideoDecoderFormat=13(→slotIndex 10), GetVideoDecoderConfigCount=14, …
    //  签名：HRESULT CheckVideoDecoderFormat(REFGUID pDecoderProfile, DXGI_FORMAT Format, BOOL* pSupported)。
    //  slotIndex = 13 − 3 = 10。返回 HRESULT，pSupported 为 BOOL*（out int，非 0 = 支持）。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int ID3D11VideoDevice_CheckVideoDecoderFormat(IntPtr self, ref Guid pDecoderProfile, int Format, out int pSupported);

    /// <summary>
    /// 决定性探测：共享 D3D11 设备是否真能为指定解码 profile 解码到 NV12 分配 DXGI 视频表面。
    /// 这是区分「真 DXGI 零拷贝」与「半 DXVA（GPU 硬解但输出读回系统内存）」的唯一权威判据。
    /// profile 须按编码选择（H264→H264_VLD_NOFGT；HEVC→HEVC_VLD_MAIN/MAIN10）。
    /// </summary>
    /// <param name="d3d11Device">共享 ID3D11Device 句柄（来自 IGpuDeviceContext.DeviceHandle）。</param>
    /// <param name="decoderProfile">要验证的 DXVA 解码 profile GUID。</param>
    /// <param name="supported">设备可分配该 profile→NV12 的 DXGI 解码表面时为 true。</param>
    /// <returns>
    /// true 表示探测本身成功执行（<paramref name="supported"/> 可信）；
    /// false 表示设备连 ID3D11VideoDevice 都不支持（即根本无视频解码能力，<paramref name="supported"/> 失真，调用方应按「不支持」处理）。
    /// </returns>
    internal static bool TryProbeDxvaSupport(IntPtr d3d11Device, Guid decoderProfile, out bool supported)
    {
        supported = false;
        if (d3d11Device == IntPtr.Zero) return false;
        Guid iid = MFConstants.IID_ID3D11VideoDevice;
        int hr = Marshal.QueryInterface(d3d11Device, in iid, out IntPtr vd);
        if (hr < 0 || vd == IntPtr.Zero)
        {
            // 设备不支持 ID3D11VideoDevice ⇒ 绝无可能做 DXVA。返回 false 让调用方判「不支持」。
            return false;
        }
        try
        {
            var check = MfVTable.Get<ID3D11VideoDevice_CheckVideoDecoderFormat>(vd, 10);
            Guid profile = decoderProfile;
            int s = 0;
            hr = check(vd, ref profile, MFConstants.DXGI_FORMAT_NV12, out s);
            if (hr < 0)
            {
                // 查询失败：保守判不支持，但探测本身是「成功执行」的（设备支持 VideoDevice，只是本次查询失败）。
                supported = false;
                return true;
            }
            supported = s != 0;
            return true;
        }
        finally
        {
            Marshal.Release(vd);
        }
    }

    /// <summary>
    /// 按视频编码选择对应的 DXVA 解码 profile（零拷贝能力探测用）。HEVC 优先 Main10、回落 Main。
    /// </summary>
    internal static Guid DxvaProfileForCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.H265 => MFConstants.D3D11_DECODER_PROFILE_HEVC_VLD_MAIN10,
        _ => MFConstants.D3D11_DECODER_PROFILE_H264_VLD_NOFGT
    };

    /// <summary>
    /// H264 专用便捷封装（保留旧调用语义）。
    /// </summary>
    internal static bool TryProbeH264DxvaSupport(IntPtr d3d11Device, out bool supported)
        => TryProbeDxvaSupport(d3d11Device, MFConstants.D3D11_DECODER_PROFILE_H264_VLD_NOFGT, out supported);

    // ── ID3D11VideoDevice 解码 profile 枚举（诊断「profile 不匹配致 CreateVideoDecoder 失败」）──
    //    GetVideoDecoderProfileCount=11(→8), GetVideoDecoderProfile=12(→9)。
    // SDK 实物 d3d11.h:13965-13967：GetVideoDecoderProfileCount 真实签名为
    //     UINT GetVideoDecoderProfileCount(THIS);  —— 直接以【返回值】返回 profile 数量（UINT），【无 out 参数】。
    //   委托签名必须是「返回值承载数量」，若误用 out 参数，真实数量会留在返回值、out 永远为 0
    //   ⇒ 误判「设备无视频解码 profile」。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint ID3D11VideoDevice_GetVideoDecoderProfileCount(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int ID3D11VideoDevice_GetVideoDecoderProfile(IntPtr self, uint index, out Guid profile);

    /// <summary>
    /// 决定性验证 DXGI 管理器是否真正绑定上了【带视频能力的】设备：从管理器内部
    /// <c>GetVideoService(IID_ID3D11VideoDevice)</c> 取回解码器实际会用的视频设备，并复测指定 profile→NV12
    /// DXVA 能力。这是区分「绑定真正生效」与「ResetDevice 静默失败（P/Invoke 偏差 / token 不匹配 /
    /// 设备缺 VIDEO_SUPPORT）」的唯一权威判据。
    /// SDK 实物 mfobjects.h:6631：<c>GetVideoService</c> 仅支持 <c>IID_ID3D11VideoDevice</c>（DXVA 服务接口），
    ///   查 <c>IID_ID3D11Device</c> 必返 E_NOINTERFACE —— 旧探针正是因此造出「绑定异常」假阴性。
    /// </summary>
    internal static string? ProbeManagerBoundDevice(IntPtr manager, Guid decoderProfile)
    {
        if (manager == IntPtr.Zero) return null;
        // 正确流程：先 OpenDeviceHandle 取 HANDLE，再 GetVideoService(hDevice, IID_ID3D11VideoDevice, …)（SDK 仅支持该 IID）。
        // SDK mfobjects.h: CloseDeviceHandle=3(→0), GetVideoService=4(→1), OpenDeviceHandle=6(→3)。
        var openHandle = MfVTable.Get<IMFDXGIDeviceManager_OpenDeviceHandle>(manager, 3); // abs 6
        int oh = openHandle(manager, out IntPtr hDevice);
        if (oh < 0 || hDevice == IntPtr.Zero)
            return $"[DXVA-DIAG] 管理器 OpenDeviceHandle 失败 HRESULT=0x{oh & 0xFFFFFFFF:X8} → ResetDevice 未真正绑定设备（或 token 不匹配），解码器将静默读回系统内存";
        try
        {
            // SDK：GetVideoService 仅支持 IID_ID3D11VideoDevice（旧探针误用 IID_ID3D11Device ⇒ 必 E_NOINTERFACE 假阴性）。
            var getVs = MfVTable.Get<IMFDXGIDeviceManager_GetVideoService>(manager, 1); // abs 4
            Guid iid = MFConstants.IID_ID3D11VideoDevice;
            int hr = getVs(manager, hDevice, ref iid, out IntPtr vd);
            if (hr < 0 || vd == IntPtr.Zero)
                return $"[DXVA-DIAG] 管理器 GetVideoService(ID3D11VideoDevice) 失败 HRESULT=0x{hr & 0xFFFFFFFF:X8} → ResetDevice 未真正绑定到带视频能力的设备（token 不匹配 / 设备缺 VIDEO_SUPPORT），解码器将静默读回系统内存";
            try
            {
                // vd 直接来自管理器内部设备，复测 profile→NV12 即等于验证「解码器经管理器取到的设备能否零拷贝解码」。
                bool capable = TryProbeDxvaSupport(vd, decoderProfile, out bool supported) && supported;
                return $"[DXVA-DIAG] 管理器取回 ID3D11VideoDevice 成功 | CheckVideoDecoderFormat(管理器内部设备, profile→NV12)={(capable ? "支持" : "不支持")} → 绑定{(capable ? "生效（解码器拿到带视频能力的设备，零拷贝前置条件齐备）" : "异常")}";
            }
            finally
            {
                Marshal.Release(vd);
            }
        }
        finally
        {
            var closeHandle = MfVTable.Get<IMFDXGIDeviceManager_CloseDeviceHandle>(manager, 0); // abs 3
            closeHandle(manager, hDevice);
        }
    }

    /// <summary>
    /// 枚举设备真实提供的视频解码 profile，并对每个 profile 复测 H264→NV12 支持。
    /// 用于诊断「MFT 实际使用的 profile 与 NOFGT 探针不一致 ⇒ CreateVideoDecoder 失败 ⇒ 静默读回」。
    /// </summary>
    internal static string? ProbeDecoderProfiles(IntPtr d3d11Device)
    {
        if (d3d11Device == IntPtr.Zero) return null;
        int hr = Marshal.QueryInterface(d3d11Device, in MFConstants.IID_ID3D11VideoDevice, out IntPtr vd);
        if (hr < 0 || vd == IntPtr.Zero) return null;
        try
        {
            var getCount = MfVTable.Get<ID3D11VideoDevice_GetVideoDecoderProfileCount>(vd, 8);
            var getProf = MfVTable.Get<ID3D11VideoDevice_GetVideoDecoderProfile>(vd, 9);
            var check = MfVTable.Get<ID3D11VideoDevice_CheckVideoDecoderFormat>(vd, 10);
            uint count = getCount(vd);
            if (count == 0)
                return $"[DXVA-DIAG] 设备无视频解码 profile（GetVideoDecoderProfileCount=0）";
            var sb = new System.Text.StringBuilder();
            sb.Append($"[DXVA-DIAG] 设备视频解码 profile 数={count}：");
            for (uint i = 0; i < count; i++)
            {
                hr = getProf(vd, i, out Guid p);
                if (hr < 0) continue;
                int s = 0;
                int ch = check(vd, ref p, MFConstants.DXGI_FORMAT_NV12, out s);
                bool nv12 = ch >= 0 && s != 0;
                bool isH264 = p == MFConstants.D3D11_DECODER_PROFILE_H264_VLD_NOFGT
                           || p == MFConstants.D3D11_DECODER_PROFILE_H264_VLD_FGT;
                sb.Append($" [{i}]{(isH264 ? "H264" : "")}{p.ToString("B").Substring(0, 8)} NV12={nv12}");
            }
            return sb.ToString();
        }
        finally
        {
            Marshal.Release(vd);
        }
    }

    // ── IDXGIDevice（dxgi.h IDXGIDeviceVtbl 实物顺序，IUnknown 之后继承 IDXGIObject）：
    //    IDXGIObject: SetPrivateData=3, SetPrivateDataInterface=4, GetPrivateData=5, GetParent=6
    //    IDXGIDevice: GetAdapter=7(→slotIndex 4), GetGPUThreadPriority=8, SetGPUThreadPriority=9, … ──
    //    IID 取自 dxgi.idl：54ec77fa-1377-44e6-8c32-88fd5f44c84c
    internal static readonly Guid IID_IDXGIDevice = new(0x54ec77fa, 0x1377, 0x44e6, 0x8c, 0x32, 0x88, 0xfd, 0x5f, 0x44, 0xc8, 0x4c);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IDXGIDevice_GetAdapter(IntPtr self, out IntPtr ppAdapter);

    // IDXGIAdapter（继承 IDXGIObject）：EnumOutputs=7, GetDesc=8(→slotIndex 5), CheckInterfaceSupport=9
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int IDXGIAdapter_GetDesc(IntPtr self, IntPtr pDesc);

    /// <summary>
    /// 决定性诊断：把共享 D3D11 设备的适配器身份摊开（是否 WARP 软件渲染 / VendorId / DeviceId）。
    /// 这是区分「真 DXVA 零拷贝」与「半 DXVA 读回」的<b>第二道成因探针</b>：
    /// 若设备落在 WARP（Microsoft Basic Render Driver，VendorId=0x1414 DeviceId=0x008C），则
    /// CheckVideoDecoderFormat 可能仍返回格式支持，但硬件视频解码引擎不存在 ⇒ 解码器静默读回系统内存
    /// （「DXVA 激活=True 却 GPU零拷贝=0」的典型成因）。此时成因是渲染器工厂用 DriverType.Hardware
    /// 选中了错误适配器，修复点=枚举 DXGI 适配器、在支持 H264 DXVA 的真实 GPU 上创建共享设备。
    /// </summary>
    /// <returns>诊断字符串（含 WARP 判定）；设备不支持 IDXGIDevice 时返回 null。</returns>
    internal static string? ProbeDeviceAdapter(IntPtr d3d11Device)
    {
        if (d3d11Device == IntPtr.Zero) return null;
        int hr = Marshal.QueryInterface(d3d11Device, in IID_IDXGIDevice, out IntPtr dxgiDevice);
        if (hr < 0 || dxgiDevice == IntPtr.Zero)
            return "[DXVA-DIAG] 共享 D3D11 设备不支持 IDXGIDevice → 无法查适配器身份（WARP 判定失败）";
        try
        {
            var getAdapter = MfVTable.Get<IDXGIDevice_GetAdapter>(dxgiDevice, 4); // abs 7
            hr = getAdapter(dxgiDevice, out IntPtr adapter);
            if (hr < 0 || adapter == IntPtr.Zero)
                return $"[DXVA-DIAG] IDXGIDevice.GetAdapter 失败 HRESULT=0x{hr & 0xFFFFFFFF:X8}";
            try
            {
                var getDesc = MfVTable.Get<IDXGIAdapter_GetDesc>(adapter, 5); // abs 8
                // DXGI_ADAPTER_DESC：Description[128]WCHAR(256B) + VendorId(4)@256 + DeviceId(4)@260 + …
                // 总长 304B（含 8B 对齐的 SIZE_T 字段）。手动读字节，避免 AOT 下 ByValTStr 封送。
                byte[] buf = new byte[304];
                GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try { hr = getDesc(adapter, h.AddrOfPinnedObject()); }
                finally { h.Free(); }
                if (hr < 0)
                    return $"[DXVA-DIAG] IDXGIAdapter.GetDesc 失败 HRESULT=0x{hr & 0xFFFFFFFF:X8}";
                uint vendor = BitConverter.ToUInt32(buf, 256);
                uint device = BitConverter.ToUInt32(buf, 260);
                bool isWarp = vendor == 0x1414 && device == 0x008C;
                return $"[DXVA-DIAG] 共享 D3D11 设备适配器 VendorId=0x{vendor:X4} DeviceId=0x{device:X4} " +
                       (isWarp ? "→ WARP(软件渲染，无硬件视频解码引擎 ⇒ 解码器必读回系统内存，零拷贝不可能)"
                               : "→ 真实硬件 GPU（DXVA 读回应是内容/解码器层问题，非适配器选错）");
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(dxgiDevice); }
    }
}
