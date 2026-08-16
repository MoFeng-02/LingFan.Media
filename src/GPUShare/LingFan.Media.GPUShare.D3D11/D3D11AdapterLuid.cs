using System;
using System.Buffers.Binary;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 取「默认 D3D11 适配器」LUID（8 字节），用于对齐 Vulkan 物理设备选择到零拷贝共享纹理所在 GPU。
/// </summary>
/// <remarks>
/// <para>以与 <c>FFmpegVideoDecoder</c> 的 D3D11VA 路径完全相同的
/// <c>D3D11CreateDevice(DriverType.Hardware, BgraSupport)</c> 创建一次性 D3D11 设备
/// （不指定适配器 → 默认适配器，即 D3D11VA 共享纹理所在 GPU），
/// 经 <c>IDXGIDevice</c> 查其所属适配器，返回 <see cref="AdapterDescription1.Luid"/> 的 8 字节小端表示。</para>
/// <para>为什么需要它：跨 GPU/跨厂商导入 D3D11 共享句柄会被驱动拒绝
/// （<c>vkAllocateMemory</c> 报 <c>ErrorOutOfDeviceMemory</c>）。把此 LUID 注入
/// <c>VulkanRendererFactory.PreferredAdapterLuid</c> 可强制 Vulkan 选与 D3D11VA 纹理同 GPU，使零拷贝成立。</para>
/// <para>Vortice.DXGI 3.8.3 实测坑：适配器描述经<b>属性</b>暴露，无 <c>GetDesc1</c> 方法；
/// <c>IDXGIAdapter1.Description1</c> 返回 <c>AdapterDescription1</c>，其 <c>Luid</c> 字段为
/// <c>Vortice.Luid</c>（<c>LowPart:uint</c>, <c>HighPart:int</c>，位于 Vortice.DirectX.dll）。</para>
/// <para>失败（无 D3D11 / 查询异常）返回 <c>null</c>，由调用方决定回落策略（通常无需对齐）。</para>
/// <para>归属：中性互操作模块 <c>LingFan.Media.GPUShare.D3D11</c>，解码后端与渲染器均可引用，
/// 不引 Platforms / Renderers / Backends，符合依赖倒置。AOT 兼容：Vortice 源生成 COM 互操作。</para>
/// </remarks>
public static class D3D11AdapterLuid
{
    /// <summary>
    /// 查询默认 D3D11 适配器 LUID（8 字节小端：LowPart@0..3 为 uint，HighPart@4..7 为 int）。
    /// 失败返回 <c>null</c>。
    /// </summary>
    public static unsafe byte[]? QueryDefaultAdapterLuid()
    {
        try
        {
            // 与 FFmpegVideoDecoder 的 D3D11VA 路径同构：D3D11CreateDevice(DriverType.Hardware) 不指定适配器
            // → 默认适配器，正是 D3D11VA 共享纹理（NV12→RGBA 转换器）所在 GPU。
            // 注：当前命名空间 LingFan.Media.GPUShare.D3D11 与 Vortice 的静态类 Vortice.Direct3D11.D3D11
            // 重名，须用完全限定名避免被解析为当前命名空间成员。
            using ID3D11Device dev = Vortice.Direct3D11.D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using IDXGIDevice dxgiDev = dev.QueryInterface<IDXGIDevice>();
            if (dxgiDev.GetAdapter(out IDXGIAdapter? adapter) != 0 || adapter is null)
                return null;
            using (adapter)
            {
                using IDXGIAdapter1 adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
                AdapterDescription1 desc = adapter1.Description1;
                byte[] luid = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(luid.AsSpan(0), desc.Luid.LowPart);
                BinaryPrimitives.WriteInt32LittleEndian(luid.AsSpan(4), desc.Luid.HighPart);
                return luid;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
