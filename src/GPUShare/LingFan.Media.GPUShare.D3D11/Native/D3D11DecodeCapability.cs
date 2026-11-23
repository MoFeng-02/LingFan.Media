using System;
using System.Runtime.InteropServices;
using LingFan.Media.Interop;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 解码能力探测：验证指定 D3D11 设备是否真能为给定解码 profile 分配 NV12 视频解码表面
/// （ID3D11VideoDevice::CheckVideoDecoderFormat，绝对槽位 13）。
/// </summary>
/// <remarks>
/// <para>用途：跨 API 零拷贝要求解码设备与渲染上下文同 GPU。当渲染侧把「窗口所在 GPU」
/// 作为对齐目标交给解码侧时，须先验证该 GPU 有真实视频解码引擎——混合显卡笔记本的核显
/// 可能只承担显示合成而无解码引擎，此时应回落独显解码 + CPU 传输，而非在注定失败的设备上初始化。</para>
/// <para>判定口径：CheckVideoDecoderFormat 仅验「profile→格式」组合支持，是设备具备该解码
/// 能力的必要判据；返回不支持即绝无 DXVA 可能。profile GUID 须与 d3d11.h 的
/// D3D11_DECODER_PROFILE_* 逐值核对（本文件常量已按权威头核对）。</para>
/// <para>AOT 兼容：手写 vtable 调用（ComVTable），零反射、零 [ComImport]。</para>
/// </remarks>
public static class D3D11DecodeCapability
{
    /// <summary>D3D11_DECODER_PROFILE_H264_VLD_NOFGT（d3d11.h 权威值）。</summary>
    public static readonly Guid ProfileH264VldNoFgt = new(0x1b81be68, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5);

    /// <summary>D3D11_DECODER_PROFILE_HEVC_VLD_MAIN（d3d11.h 权威值）。</summary>
    public static readonly Guid ProfileHevcVldMain = new(0x5b11d51b, 0x2f4c, 0x4452, 0xbc, 0xc3, 0x09, 0xf2, 0xa1, 0x16, 0x0c, 0xc0);

    /// <summary>D3D11_DECODER_PROFILE_HEVC_VLD_MAIN10（d3d11.h 权威值）。</summary>
    public static readonly Guid ProfileHevcVldMain10 = new(0x107af0e0, 0xef1a, 0x4d19, 0xab, 0xa8, 0x67, 0xa1, 0x63, 0x07, 0x3d, 0x13);

    /// <summary>DXGI_FORMAT_NV12（dxgiformat.h 权威值 = 103）。</summary>
    private const int DxgiFormatNv12 = 103;

    // ID3D11VideoDevice::CheckVideoDecoderFormat（绝对槽位 13）。
    // 签名：HRESULT CheckVideoDecoderFormat(REFGUID pDecoderProfile, DXGI_FORMAT Format, BOOL* pSupported)。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PFN_CheckVideoDecoderFormat(IntPtr self, ref Guid pDecoderProfile, int format, out int pSupported);

    /// <summary>
    /// 探测设备能否为指定解码 profile 解码到 NV12。
    /// </summary>
    /// <param name="devicePtr">ID3D11Device 原生指针。</param>
    /// <param name="decoderProfile">DXVA 解码 profile GUID（如 <see cref="ProfileH264VldNoFgt"/>）。</param>
    /// <returns><see langword="true"/>=设备支持该 profile→NV12 解码；设备无视频接口、查询失败或不支持均为 <see langword="false"/>。</returns>
    public static bool SupportsDecodeToNv12(IntPtr devicePtr, Guid decoderProfile)
    {
        if (devicePtr == IntPtr.Zero)
            return false;
        if (!D3D11Interop.TryQueryInterface(devicePtr, D3D11Interop.IID_ID3D11VideoDevice, out IntPtr videoDevice))
            return false;
        try
        {
            var check = ComVTable.GetAbsolute<PFN_CheckVideoDecoderFormat>(videoDevice, 13);
            Guid profile = decoderProfile;
            int hr = check(videoDevice, ref profile, DxgiFormatNv12, out int supported);
            return hr >= 0 && supported != 0;
        }
        catch
        {
            // vtable 解析/调用异常按不支持处理：探测失败不得让调用方在未知能力的设备上初始化。
            return false;
        }
        finally
        {
            D3D11Interop.Release(videoDevice);
        }
    }
}