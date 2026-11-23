using System;
using System.Buffers.Binary;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 按「GL 渲染器字符串」匹配 DXGI 适配器：返回该 GL 上下文所在 GPU 的适配器 LUID。
/// </summary>
/// <remarks>
/// <para>用途：Windows WGL 的 GL 上下文被平台钉在窗口所在显示器的 GPU 上，渲染器无法选卡；
/// 跨 API 零拷贝须让解码设备反向对齐到这张卡。GL 侧只有 <c>glGetString(GL_RENDERER)</c> 字符串
/// 可标识 GPU，本类把它与 DXGI 适配器描述做厂商族匹配（NVIDIA / AMD / Intel），命中唯一适配器时返回其 LUID。</para>
/// <para><b>匹配口径</b>：GL_RENDERER 只含厂商族信息，同厂商多适配器（含同一 GPU 的多输出头登记、
/// 或双显卡坞）无法凭字符串区分。消歧锚点取「默认 D3D11 适配器」——Windows 桌面窗口默认落在
/// 主显示器 GPU，WGL 离屏/窗口上下文与它同卡；故同厂商候选中优先取与默认适配器 LUID 相同者。
/// 唯一同厂商候选直接命中；多候选且默认适配器不在其中时返回 <see langword="null"/>——宁可不给提示，也不给错卡。</para>
/// <para>AOT 兼容：复用 <see cref="D3D11Interop"/> 手写 vtable，零反射。</para>
/// </remarks>
public static class D3D11AdapterByGlRenderer
{
    /// <summary>厂商 PCI Vendor ID：NVIDIA。</summary>
    private const uint VendorNvidia = 0x10DE;

    /// <summary>厂商 PCI Vendor ID：AMD（含旧 ATI）。</summary>
    private const uint VendorAmd = 0x1002;

    /// <summary>厂商 PCI Vendor ID：Intel。</summary>
    private const uint VendorIntel = 0x8086;

    /// <summary>
    /// 按 GL_RENDERER 字符串查找同厂商 DXGI 适配器的 LUID（8 字节小端）。
    /// </summary>
    /// <param name="glRenderer">GL 渲染器字符串（<c>glGetString(GL_RENDERER)</c> 返回值）。</param>
    /// <returns>唯一命中适配器的 LUID；GL 字符串为空、厂商不可判、无命中或同厂商多适配器时为 <see langword="null"/>。</returns>
    public static byte[]? TryMatchAdapterLuid(string? glRenderer)
    {
        uint vendor = ClassifyVendor(glRenderer);
        if (vendor == 0)
            return null;

        IntPtr factory = IntPtr.Zero;
        try
        {
            Guid iid = D3D11Interop.IID_IDXGIFactory1;
            factory = D3D11Interop.CreateDXGIFactory1(iid);
            byte[]? found = null;
            int distinctCount = 0;
            byte[]? defaultLuid = D3D11AdapterLuid.QueryDefaultAdapterLuid();
            byte[]? defaultMatch = null;
            for (uint i = 0; ; i++)
            {
                IntPtr adapter = D3D11Interop.TryEnumAdapter1(factory, i);
                if (adapter == IntPtr.Zero)
                    break;
                try
                {
                    var (adapterVendor, _, _, flags) = D3D11Interop.GetAdapterSummary(adapter);
                    if ((flags & D3D11Interop.DxgiAdapterFlagSoftware) != 0)
                        continue;
                    if (adapterVendor != vendor)
                        continue;
                    IntPtr adapter1 = D3D11Interop.QueryInterface(adapter, D3D11Interop.IID_IDXGIAdapter1);
                    byte[] luid;
                    try
                    {
                        D3D11Interop.GetDesc1Luid(adapter1, out uint low, out int high);
                        luid = new byte[8];
                        BinaryPrimitives.WriteUInt32LittleEndian(luid.AsSpan(0), low);
                        BinaryPrimitives.WriteInt32LittleEndian(luid.AsSpan(4), high);
                    }
                    finally
                    {
                        D3D11Interop.Release(adapter1);
                    }
                    if (found == null || !luid.AsSpan().SequenceEqual(found))
                    {
                        distinctCount++;
                        found = luid;
                    }
                    if (defaultLuid != null && luid.AsSpan().SequenceEqual(defaultLuid))
                        defaultMatch = luid;
                }
                finally
                {
                    D3D11Interop.Release(adapter);
                }
            }
            if (distinctCount == 1)
                return found;
            // 多张同厂商卡：GL 字符串无法区分，用「默认适配器=主显示器 GPU」消歧（WGL 窗口上下文与其同卡）。
            return defaultMatch;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory != IntPtr.Zero)
                D3D11Interop.Release(factory);
        }
    }

    /// <summary>按 GL_RENDERER 字符串判定厂商族（不可判返回 0）。</summary>
    private static uint ClassifyVendor(string? glRenderer)
    {
        if (string.IsNullOrEmpty(glRenderer))
            return 0;
        if (glRenderer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return VendorNvidia;
        if (glRenderer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || glRenderer.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || glRenderer.Contains("ATI", StringComparison.OrdinalIgnoreCase))
            return VendorAmd;
        if (glRenderer.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return VendorIntel;
        return 0;
    }
}