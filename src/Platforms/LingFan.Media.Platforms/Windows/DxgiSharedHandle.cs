using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// DXGI 共享句柄生命周期封装（<see cref="SafeHandle"/>）。
/// </summary>
/// <remarks>
/// <para>管理 <c>IDXGIResource::GetSharedHandle</c> 返回的 HANDLE，Dispose / GC 终结时 <c>CloseHandle</c> 释放，防止句柄泄漏。</para>
/// <para><b>分工</b>：创建共享纹理与 KeyedMutex 同步由 <see cref="D3D11Interop"/> 负责（R12），
/// 本类只封装共享句柄的生命周期（R14 核心目标：<b>正确管理共享句柄生命周期</b>）。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——原生句柄释放是 COM/Win32 同步边界，无 I/O await。</para>
/// <para>AOT 兼容：<c>CloseHandle</c> 用 <see langword="LibraryImport"/> 源生成 P/Invoke（零反射、NativeAOT 友好）。</para>
/// </remarks>
public sealed partial class DxgiSharedHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private DxgiSharedHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    /// <summary>从 D3D11 纹理创建并封装 DXGI 共享 NT 句柄（纹理须带 <see cref="ResourceOptionFlags.SharedNTHandle"/>）。</summary>
    /// <remarks>用 <c>IDXGIResource1.CreateSharedHandle</c> 创建真 NT 句柄——只有 NT 句柄可被
    /// <c>CloseHandle</c> 正确释放（legacy GetSharedHandle 伪句柄不可 Close）。</remarks>
    /// <param name="texture">源纹理。</param>
    /// <returns>封装的共享句柄（调用方负责 Dispose）。</returns>
    public static DxgiSharedHandle FromTexture(ID3D11Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        using var dxgiResource1 = texture.QueryInterface<IDXGIResource1>();
        IntPtr handle = dxgiResource1.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
        return new DxgiSharedHandle(handle);
    }

    /// <summary>从共享句柄打开 D3D11 纹理（跨进程共享）。</summary>
    /// <param name="device">目标 D3D11 设备。</param>
    /// <returns>打开的共享纹理（调用方负责释放）。</returns>
    public ID3D11Texture2D OpenSharedTexture(ID3D11Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (IsInvalid) throw new InvalidOperationException("共享句柄无效。");

        var device1 = device.QueryInterface<ID3D11Device1>();
        try
        {
            device1.OpenSharedResource1<ID3D11Texture2D>(handle, out var texture);
            return texture ?? throw new InvalidOperationException("打开共享纹理失败。");
        }
        finally
        {
            device1.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (handle == IntPtr.Zero) return true;
        return CloseHandle(handle);
    }

    /// <summary>Win32 CloseHandle（源生成 P/Invoke，AOT 友好）。</summary>
    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
