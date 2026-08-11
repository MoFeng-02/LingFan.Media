using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Decoders;

namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// 从 MF 样本 buffer 提取 DXGI 纹理（零拷贝）的共用助手。
/// </summary>
/// <remarks>
/// <para><b>由来</b>：原逻辑内嵌在 <c>MFVideoDecoder.TryExtractDxgiTexture</c>。A 方案（SourceReader 自带硬解）
/// 后 <c>MFDemuxer</c> 也需在读样阶段提取纹理，故抽为无状态静态助手供两侧共用，避免两份分叉实现漂移。</para>
/// <para><b>路径</b>：<c>QueryInterface(IMFDXGIBuffer)</c> → <c>GetResource(ID3D11Texture2D)</c> +
/// <c>GetSubresourceIndex</c> → 包成 <see cref="MfD3D11TextureResource"/>（<see cref="IGpuTextureResource"/> 中立契约）。</para>
/// <para><b>COM 配对</b>：<c>GetResource</c> 成功即已 AddRef 纹理；后续任一步失败必须 <c>Marshal.Release(tex)</c>，
/// 否则纹理引用泄漏（GPU 显存不回收）。QI 得到的 <c>IMFDXGIBuffer</c> 无论成败都在 finally 释放。</para>
/// <para><b>失败语义</b>：一律返回 <see langword="null"/> 并回填 HRESULT，绝不抛异常——
/// 调用方据此回落 CPU 路径（设计原则：硬解优先、软解兜底，失败不得炸链路）。</para>
/// <para><b>AOT 兼容</b>：原始 vtable 委托 + <c>Marshal.QueryInterface</c>，无反射、无 <c>[ComImport]</c>。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class MfDxgiTextureExtractor
{
    /// <summary>
    /// 尝试把 MF 样本 buffer 提取为 GPU 纹理资源（零拷贝）。
    /// </summary>
    /// <param name="buffer">IMFMediaBuffer*（来自 <c>IMFSample.GetBufferByIndex(0)</c>）。</param>
    /// <param name="width">帧宽（显示尺寸）。</param>
    /// <param name="height">帧高（显示尺寸）。</param>
    /// <param name="format">像素格式（DXVA 标准输出为 <see cref="PixelFormat.NV12"/>）。</param>
    /// <param name="hresult">回填最后一次失败的 HRESULT（成功为 0），供调用方一次性诊断日志使用。</param>
    /// <returns>成功返回持有纹理引用的资源；不是 DXGI buffer 或提取失败返回 <see langword="null"/>。</returns>
    /// <remarks>同步（native 分类）：全部为 COM 调用，无 I/O await，不补 async。</remarks>
    internal static MfD3D11TextureResource? TryExtract(
        IntPtr buffer, int width, int height, PixelFormat format, out int hresult)
    {
        hresult = 0;
        if (buffer == IntPtr.Zero || width <= 0 || height <= 0)
        {
            hresult = unchecked((int)0x80070057); // E_INVALIDARG
            return null;
        }

        Guid iidDxgi = MFConstants.IID_IMFDXGIBuffer;
        int hr = Marshal.QueryInterface(buffer, in iidDxgi, out IntPtr dxgi);
        if (hr < 0 || dxgi == IntPtr.Zero)
        {
            // 非 DXGI buffer ⇒ MFT 把帧读回了系统内存（"半 DXVA"）。调用方负责一次性深度诊断，此处静默回落。
            hresult = hr;
            return null;
        }

        IntPtr tex = IntPtr.Zero;
        try
        {
            var getResource = MfVTable.Get<MfDxvaInterop.IMFDXGIBuffer_GetResource>(dxgi, 0);   // 绝对槽 3
            var getSub = MfVTable.Get<MfDxvaInterop.IMFDXGIBuffer_GetSubresourceIndex>(dxgi, 1); // 绝对槽 4

            Guid iidTex = MFConstants.IID_ID3D11Texture2D;
            hr = getResource(dxgi, ref iidTex, out tex);
            if (hr < 0 || tex == IntPtr.Zero)
            {
                hresult = hr;
                tex = IntPtr.Zero;
                return null;
            }

            hr = getSub(dxgi, out uint sub);
            if (hr < 0)
            {
                hresult = hr;
                return null; // finally 中释放 tex（GetResource 已 AddRef）
            }

            var resource = new MfD3D11TextureResource(tex, width, height, format, (int)sub);
            tex = IntPtr.Zero; // 所有权已转移给 resource，finally 不再释放
            return resource;
        }
        catch (Exception)
        {
            hresult = unchecked((int)0x80004005); // E_FAIL：vtable 取委托/调用异常，回落 CPU 路径
            return null;
        }
        finally
        {
            // COM 配对：未成功转移所有权的纹理引用必须释放，否则显存泄漏
            if (tex != IntPtr.Zero) Marshal.Release(tex);
            Marshal.Release(dxgi);
        }
    }
}
