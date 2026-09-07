using System;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// D3D11 纹理 → DXGI 共享 NT 句柄的中性封装（全手写原生 vtable，无 Vortice、无反射）。
/// </summary>
/// <remarks>
/// <para>供解码后端（FFmpeg / MF）与渲染器侧共享纹理导入复用：把 D3D11 纹理经
/// <c>IDXGIResource1::CreateSharedHandle</c>（绝对槽位 13）导出为可被跨 API 设备（Vulkan / GL / D3D11）
/// 经 <c>OpenSharedResource1</c> 打开的 NT 共享句柄。</para>
/// <para><b>前置条件</b>：源纹理须带 SharedNTHandle 创建（<c>OpenSharedResource1</c> 仅接受 NT 句柄，
/// legacy <c>GetSharedHandle</c> 伪句柄不可用于跨 API）。注意：NV12 等视频格式<b>不可</b>带 SharedKeyedMutex
/// （会导致 CreateSharedHandle 返回 DXGI_ERROR_INVALID_CALL）；RGBA 等可渲染格式可带。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：<see cref="GetSharedHandle"/> 经
/// <c>IDXGIResource1::CreateSharedHandle</c> 导出内核 NT 句柄，并把<b>所有权转移</b>给调用方（导入生产者）。
/// 本方法<b>不</b>关闭句柄；调用方须在导入成功与失败后均 <c>CloseHandle</c>（OpenSharedResource1 /
/// vkAllocateMemory 已建立独立资源引用，关闭句柄不销毁资源）。单一责任人避免与导出方重复关闭（双关）。</para>
/// <para>AOT 兼容：全手写原生 vtable P/Invoke（见 <see cref="D3D11Interop"/>），零反射、零 [ComImport]、零 Vortice。</para>
/// </remarks>
internal static class D3D11SharedHandle
{
    /// <summary>从 D3D11 纹理导出 DXGI 共享 NT 句柄。</summary>
    /// <param name="texturePtr">源纹理裸指针（须带 SharedNTHandle）。</param>
    /// <returns>共享句柄；导出失败抛 <see cref="InvalidOperationException"/> 或 <see cref="COMException"/>。</returns>
    public static IntPtr GetSharedHandle(IntPtr texturePtr)
    {
        if (texturePtr == IntPtr.Zero)
            throw new ArgumentNullException(nameof(texturePtr));

        // QI 源纹理为 IDXGIResource1（须以 SharedNTHandle 创建），再导出 NT 共享句柄。
        IntPtr dxgiRes1 = D3D11Interop.QueryInterface(texturePtr, D3D11Interop.IID_IDXGIResource1);
        try
        {
            // dwAccess = Read | Write（与解码侧导出语义一致）；pDevice = null（不限设备）。
            return D3D11Interop.CreateSharedHandle(dxgiRes1, D3D11Interop.SharedResourceReadWrite);
        }
        finally
        {
            D3D11Interop.Release(dxgiRes1);
        }
    }
}
