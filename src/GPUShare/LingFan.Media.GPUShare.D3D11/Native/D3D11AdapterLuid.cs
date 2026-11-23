using System;
using System.Buffers.Binary;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 取「默认 D3D11 适配器」LUID（8 字节），用于对齐 Vulkan 物理设备选择到零拷贝共享纹理所在 GPU。
/// </summary>
/// <remarks>
/// <para>以与 <c>FFmpegVideoDecoder</c> 的 D3D11VA 路径完全相同的
/// <c>D3D11CreateDevice(DriverType.Hardware, BgraSupport)</c> 创建一次性 D3D11 设备
/// （不指定适配器 → 默认适配器，即 D3D11VA 共享纹理所在 GPU），
/// 经 <c>IDXGIDevice</c> 查其所属适配器，返回 <c>DXGI_ADAPTER_DESC1.LUID</c> 的 8 字节小端表示。</para>
/// <para>为什么需要它：跨 GPU/跨厂商导入 D3D11 共享句柄会被驱动拒绝
/// （<c>vkAllocateMemory</c> 报 <c>ErrorOutOfDeviceMemory</c>）。把此 LUID 注入
/// <c>VulkanRendererFactory.PreferredAdapterLuid</c> 可强制 Vulkan 选与 D3D11VA 纹理同 GPU，使零拷贝成立。</para>
/// <para>失败（无 D3D11 / 查询异常 / vtable 校准失败）返回 <c>null</c>，由调用方决定回落策略（通常无需对齐）。</para>
/// <para>归属：中性互操作模块 <c>LingFan.Media.GPUShare.D3D11</c>，解码后端与渲染器均可引用，
/// 不引 Platforms / Renderers / Backends，符合依赖倒置。</para>
/// <para>AOT 兼容：全手写原生 vtable P/Invoke（见 <see cref="D3D11Interop"/>），零反射、零 [ComImport]、零 Vortice。</para>
/// </remarks>
public static class D3D11AdapterLuid
{
    /// <summary>
    /// 查询默认 D3D11 适配器 LUID（8 字节小端：LowPart@0..3 为 uint，HighPart@4..7 为 int）。
    /// 失败返回 <c>null</c>。
    /// </summary>
    public static byte[]? QueryDefaultAdapterLuid()
    {
        try
        {
            // 与 FFmpegVideoDecoder 的 D3D11VA 路径同构：D3D11CreateDevice(DriverType.Hardware) 不指定适配器
            // → 默认适配器，正是 D3D11VA 共享纹理（NV12→RGBA 转换器）所在 GPU。
            D3D11Interop.D3D11CreateDevice(out IntPtr dev, out IntPtr ctx);
            try
            {
                // vtable 校准：确保本模块解析的 vtable 槽位（含 Flush 111 / VideoProcessorBlt 53 等）真实有效。
                D3D11Interop.VerifyVtableLayout(dev, ctx);

                IntPtr dxgiDevPtr = D3D11Interop.QueryInterface(dev, D3D11Interop.IID_IDXGIDevice);
                try
                {
                    IntPtr adapterPtr = D3D11Interop.GetAdapter(dxgiDevPtr);
                    if (adapterPtr == IntPtr.Zero)
                        return null;

                    try
                    {
                        IntPtr adapter1Ptr = D3D11Interop.QueryInterface(adapterPtr, D3D11Interop.IID_IDXGIAdapter1);
                        try
                        {
                            D3D11Interop.GetDesc1Luid(adapter1Ptr, out uint low, out int high);
                            byte[] luid = new byte[8];
                            BinaryPrimitives.WriteUInt32LittleEndian(luid.AsSpan(0), low);
                            BinaryPrimitives.WriteInt32LittleEndian(luid.AsSpan(4), high);
                            return luid;
                        }
                        finally
                        {
                            D3D11Interop.Release(adapter1Ptr);
                        }
                    }
                    finally
                    {
                        D3D11Interop.Release(adapterPtr);
                    }
                }
                finally
                {
                    D3D11Interop.Release(dxgiDevPtr);
                }
            }
            finally
            {
                // 本模块创建的临时设备/上下文引用计数已为 1，须显式 Release 闭环。
                D3D11Interop.Release(dev);
                D3D11Interop.Release(ctx);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 查询「首选适配器」（独显优先，与 <see cref="D3D11Interop.FindPreferredAdapter"/> 同一策略）的
    /// LUID（8 字节小端）与描述。失败返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="QueryDefaultAdapterLuid"/> 的区别：后者取「默认适配器」（主显示器所在 GPU，
    /// 混合显卡上常为核显）；本方法取「独显优先」适配器——FFmpeg D3D11VA 自有设备与 OpenGL 桥接
    /// 设备均按此策略创建，跨 API 零拷贝导入须与它们对齐到同一 GPU，而非默认适配器。
    /// </remarks>
    /// <returns>LUID 与适配器描述（含 vendor）；无可用适配器或查询异常返回 <c>null</c>。</returns>
    public static (byte[] Luid, uint Vendor, string Description)? QueryPreferredAdapter()
    {
        IntPtr adapter = IntPtr.Zero;
        try
        {
            adapter = D3D11Interop.FindPreferredAdapter();
            if (adapter == IntPtr.Zero)
                return null;

            IntPtr adapter1 = D3D11Interop.QueryInterface(adapter, D3D11Interop.IID_IDXGIAdapter1);
            try
            {
                D3D11Interop.GetDesc1Luid(adapter1, out uint low, out int high);
                byte[] luid = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(luid.AsSpan(0), low);
                BinaryPrimitives.WriteInt32LittleEndian(luid.AsSpan(4), high);
                var (vendor, desc) = D3D11Interop.GetAdapterVendorAndDescription(adapter1);
                return (luid, vendor, desc);
            }
            finally
            {
                D3D11Interop.Release(adapter1);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (adapter != IntPtr.Zero)
                D3D11Interop.Release(adapter);
        }
    }
}
