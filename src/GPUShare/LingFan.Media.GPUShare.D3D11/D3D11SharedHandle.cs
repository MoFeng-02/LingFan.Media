using System;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// D3D11 纹理 → DXGI 共享 NT 句柄的中性封装（Vortice 干净调用，无 raw vtable）。
/// </summary>
/// <remarks>
/// <para>供解码后端（FFmpeg / MF）与渲染器侧共享纹理导入复用：把 D3D11 纹理经
/// <c>IDXGIResource1::CreateSharedHandle</c> 导出为可被跨 API 设备（Vulkan / GL / D3D11）
/// 经 <c>OpenSharedResource1</c> 打开的 NT 共享句柄。</para>
/// <para><b>前置条件</b>：源纹理须以 <see cref="ResourceOptionFlags.SharedNTHandle"/> 创建
/// （跨 API 共享惯例，见 <c>D3D11Interop</c>：<c>SharedKeyedMutex | SharedNTHandle</c>；
/// <c>OpenSharedResource1</c> 仅接受 NT 句柄，legacy <c>GetSharedHandle</c> 伪句柄不可用于跨 API）。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：<see cref="GetSharedHandle"/> 经
/// <c>IDXGIResource1::CreateSharedHandle</c> 导出内核 NT 句柄，并把<b>所有权转移</b>给调用方（导入生产者）。
/// 本方法<b>不</b>关闭句柄；调用方须在导入成功与失败后均 <c>CloseHandle</c>（OpenSharedResource1 /
/// vkAllocateMemory 已建立独立资源引用，关闭句柄不销毁资源）。单一责任人避免与导出方重复关闭（双关）。</para>
/// <para>AOT 兼容：Vortice 源生成 COM 互操作（零反射、NativeAOT 友好），无 [ComImport]。</para>
/// </remarks>
internal static class D3D11SharedHandle
{
    /// <summary>从 D3D11 纹理导出 DXGI 共享 NT 句柄。</summary>
    /// <param name="texture">源纹理（须带 <see cref="ResourceOptionFlags.SharedNTHandle"/>）。</param>
    /// <returns>共享句柄；导出失败抛 <see cref="InvalidOperationException"/>。</returns>
    public static IntPtr GetSharedHandle(ID3D11Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        using var dxgi = texture.QueryInterface<IDXGIResource1>();
        // dwAccess = Read | Write（与解码侧导出语义一致）；pDevice = null（不限设备）。
        return dxgi.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
    }
}
