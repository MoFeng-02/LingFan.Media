using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Interop;

namespace LingFan.Media.GPUShare.D3D11;

// D3D11Interop 的 Shared 功能域公开互操作原语（部分类，由 D3D11Interop.cs 按域拆分，编译期等价）。
// IID/槽位常量、PFN 委托与平面导出函数在 D3D11Interop.cs；vtable 槽位不可手算、须运行时校准。
public static unsafe partial class D3D11Interop
{
    /// <summary>IDXGIResource1::CreateSharedHandle（绝对槽位 13）；pAttributes=null、lpName=null、dwAccess=access。</summary>
    /// <remarks>dwAccess 须用 SDK dxgi.h 权威值组合：<see cref="SharedResourceReadWrite"/>
    /// （READ/WRITE 位定义见 D3D11Interop.cs 的 <c>DXGI_SHARED_RESOURCE_READ</c>/<c>DXGI_SHARED_RESOURCE_WRITE</c> 常量）。
    /// 防回归：READ/WRITE 直觉序（1/2）是错的——2 不是任何 DXGI 共享资源标志。</remarks>
    public static IntPtr CreateSharedHandle(IntPtr dxgiResource1Ptr, uint access)
    {
        var fn = ComVTable.GetAbsolute<PFN_CreateSharedHandle>(dxgiResource1Ptr, 13);
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
        var fn = ComVTable.GetAbsolute<PFN_AcquireSync>(keyedMutexPtr, 8);
        int hr = fn(keyedMutexPtr, key, milliseconds);
        if (hr != 0)
            throw new COMException($"AcquireSync 失败/超时 (0x{hr:X8})", hr);
    }

    /// <summary>IDXGIKeyedMutex::ReleaseSync（绝对槽位 9）。</summary>
    public static void ReleaseSync(IntPtr keyedMutexPtr, ulong key)
    {
        var fn = ComVTable.GetAbsolute<PFN_ReleaseSync>(keyedMutexPtr, 9);
        int hr = fn(keyedMutexPtr, key);
        if (hr != 0)
            throw new COMException($"ReleaseSync 失败 (0x{hr:X8})", hr);
    }

    /// <summary>IDXGIDevice::GetAdapter（绝对槽位 7）。</summary>
    public static IntPtr GetAdapter(IntPtr dxgiDevicePtr)
    {
        var fn = ComVTable.GetAbsolute<PFN_GetAdapter>(dxgiDevicePtr, 7);
        int hr = fn(dxgiDevicePtr, out IntPtr pp);
        if (hr < 0)
            throw new COMException($"GetAdapter 失败 (0x{hr:X8})", hr);
        return pp;
    }

    /// <summary>
    /// 查询设备所属 GPU 适配器的 vendor+描述（设备→IDXGIDevice(QI)→GetAdapter(槽7)→GetDesc1(槽10)）。
    /// 用于确证「设备实际落在哪张 GPU」。
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
            var fn = ComVTable.GetAbsolute<PFN_GetDesc1>(adapter1Ptr, 10);
            int hr = fn(adapter1Ptr, (IntPtr)p);
            if (hr < 0)
                throw new COMException($"GetDesc1 失败 (0x{hr:X8})", hr);
        }

        luidLow = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(296));
        luidHigh = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(300));
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
    /// （独显 &gt;&gt; 集显；无独显时退化为显存最大的集显）。不绑定任何厂商/型号。
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

    /// <summary>
    /// 读适配器的 vendor 与描述（IDXGIAdapter1::GetDesc1，绝对槽位 10）。
    /// 供跨 API 对齐诊断确认「首选适配器是哪张卡」。
    /// </summary>
    /// <param name="adapter1Ptr">IDXGIAdapter1 接口指针。</param>
    /// <returns>VendorId 与适配器描述字符串。</returns>
    public static (uint Vendor, string Description) GetAdapterVendorAndDescription(IntPtr adapter1Ptr)
    {
        var info = GetAdapterDesc1Info(adapter1Ptr);
        return (info.Vendor, info.Description);
    }

    /// <summary>DXGI_ADAPTER_DESC1.Flags 的软件适配器位（Microsoft Basic Render Driver）。</summary>
    public const uint DxgiAdapterFlagSoftware = DXGI_ADAPTER_FLAG_SOFTWARE;

    /// <summary>创建 IDXGIFactory1（失败抛 COMException）。调用方负责 <see cref="Release"/>。</summary>
    /// <param name="iid">IDXGIFactory1 的 IID（<see cref="IID_IDXGIFactory1"/>）。</param>
    /// <returns>工厂接口指针。</returns>
    public static IntPtr CreateDXGIFactory1(Guid iid)
    {
        int hr = CreateDXGIFactory1Native(ref iid, out IntPtr factory);
        if (hr < 0 || factory == IntPtr.Zero)
            throw new COMException($"CreateDXGIFactory1 失败 (0x{hr:X8})", hr);
        return factory;
    }

    /// <summary>枚举第 <paramref name="index"/> 个 DXGI 适配器；无更多或失败返回 <see cref="IntPtr.Zero"/>。
    /// 成功时调用方负责 <see cref="Release"/>。</summary>
    /// <param name="factoryPtr">IDXGIFactory1 接口指针。</param>
    /// <param name="index">适配器序号（从 0 起）。</param>
    /// <returns>适配器接口指针；无更多为 Zero。</returns>
    public static IntPtr TryEnumAdapter1(IntPtr factoryPtr, uint index)
    {
        int hr = EnumAdapters1(factoryPtr, index, out IntPtr adapter);
        return hr < 0 ? IntPtr.Zero : adapter;
    }

    /// <summary>读适配器摘要（vendor / 描述 / 专用显存 / Flags），供选卡与对齐匹配。</summary>
    /// <param name="adapterPtr">IDXGIAdapter（1）接口指针。</param>
    /// <returns>四元组摘要。</returns>
    public static (uint Vendor, string Description, ulong DedicatedVideoMemory, uint Flags) GetAdapterSummary(IntPtr adapterPtr)
        => GetAdapterDesc1Info(adapterPtr);

    /// <summary>按 LUID（8 字节小端）查找 DXGI 适配器；未命中返回 <see cref="IntPtr.Zero"/>。
    /// 成功时调用方负责 <see cref="Release"/>。</summary>
    /// <param name="luid">目标适配器 LUID。</param>
    /// <returns>适配器接口指针；未命中为 Zero。</returns>
    public static IntPtr FindAdapterByLuid(byte[] luid)
    {
        if (luid is not { Length: 8 })
            return IntPtr.Zero;
        uint wantLow = BinaryPrimitives.ReadUInt32LittleEndian(luid.AsSpan(0));
        int wantHigh = BinaryPrimitives.ReadInt32LittleEndian(luid.AsSpan(4));
        IntPtr factory = CreateDXGIFactory1(IID_IDXGIFactory1);
        try
        {
            for (uint i = 0; ; i++)
            {
                IntPtr adapter = TryEnumAdapter1(factory, i);
                if (adapter == IntPtr.Zero)
                    return IntPtr.Zero;
                IntPtr adapter1 = QueryInterface(adapter, IID_IDXGIAdapter1);
                bool hit;
                try
                {
                    GetDesc1Luid(adapter1, out uint low, out int high);
                    hit = low == wantLow && high == wantHigh;
                }
                finally
                {
                    Release(adapter1);
                }
                if (hit)
                    return adapter;
                Release(adapter);
            }
        }
        finally
        {
            Release(factory);
        }
    }

    /// <summary>IDXGIFactory1::EnumAdapters1（绝对槽位 12）。返回 HRESULT（DXGI_ERROR_NOT_FOUND=无更多）。</summary>
    private static int EnumAdapters1(IntPtr factoryPtr, uint adapterIndex, out IntPtr ppAdapter)
    {
        var fn = ComVTable.GetAbsolute<PFN_EnumAdapters1>(factoryPtr, 12);
        return fn(factoryPtr, adapterIndex, out ppAdapter);
    }

    /// <summary>
    /// 读 DXGI_ADAPTER_DESC1 关键字段——GetDesc1 绝对槽位 10。
    /// 布局（308 字节）：Description @0（WCHAR[128]，256B）→ VendorId @256（UINT）→ DeviceId @260 →
    /// SubSysId @264 → Revision @268 → DedicatedVideoMemory @272（SIZE_T，x64 8B）→
    /// DedicatedSystemMemory @280 → SharedSystemMemory @288 → AdapterLuid @296（8B）→ Flags @304（UINT）。
    /// 注：VendorId 不在 @0——@0 是 WCHAR[128] 描述（此布局易误判，已按 ABI 核对）。
    /// </summary>
    private static (uint Vendor, string Description, ulong DedicatedVideoMemory, uint Flags) GetAdapterDesc1Info(IntPtr adapterPtr)
    {
        Span<byte> buf = stackalloc byte[308];
        fixed (byte* p = buf)
        {
            var fn = ComVTable.GetAbsolute<PFN_GetDesc1>(adapterPtr, 10);
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


    // 运行时扫描校准（槽位错 → 立即可诊断失败，绝不带野指针继续调）
}
