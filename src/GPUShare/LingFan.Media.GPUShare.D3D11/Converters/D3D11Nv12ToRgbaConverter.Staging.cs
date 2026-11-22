using System;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 中转纹理分部：非共享 BGRA staging 与普通 NV12 单张纹理的懒创建/尺寸重建。
/// </summary>
public sealed partial class D3D11Nv12ToRgbaConverter
{
    // 非共享 BGRA 中转纹理（staging）：直写共享纹理的 Blt 失败时启用（部分驱动上 VP 拒绝直写
    // SharedKeyedMutex|SharedNTHandle 纹理 → E_INVALIDARG）。Blt 先写 staging，再
    // CopySubresourceRegion 拷入共享输出纹理——全程 GPU 内，零 CPU 回读。跨帧复用（尺寸变了才重建）。
    private IntPtr _stagingPtr;
    private int _stagingWidth = -1;
    private int _stagingHeight = -1;
    // 直写共享纹理 Blt 已失败标志：置 true 后后续帧直接走 staging，避免每帧重复失败开销。
    private bool _directBltFailed;
    // staging 路径首次启用已通告标志（一次性诊断打印，防刷屏）。
    private bool _stagingAnnounced;

    // 普通 NV12 中转纹理（非数组、非 DECODER 绑定）：把 ffmpeg 硬解切片经 CopySubresourceRegion
    // 拷入此纹理，再用它做 VP 输入——绕过驱动对 D3D11_BIND_DECODER + Texture2DArray 纹理的 VP Blt 限制。
    private IntPtr _singleNv12Ptr;
    private int _singleNv12Width = -1;
    private int _singleNv12Height = -1;

    /// <summary>确保非共享 BGRA 中转纹理存在（尺寸变化时重建）。</summary>
    private void EnsureStaging(int width, int height)
    {
        if (_stagingPtr != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
            return;

        if (_stagingPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_stagingPtr);
            _stagingPtr = IntPtr.Zero;
        }

        var stagingDesc = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11Interop.FormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = 0, // D3D11_USAGE_DEFAULT
            BindFlags = D3D11Interop.BindRenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = 0, // 非共享（关键差异：无 SharedKeyedMutex/SharedNTHandle）
        };
        _stagingPtr = D3D11Interop.CreateTexture2D(_devicePtr, stagingDesc);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>确保普通 NV12 中转纹理存在（尺寸变化时重建）。</summary>
    /// <remarks>
    /// 普通 NV12 纹理 = ArraySize=1, BindFlags=0, MiscFlags=0——无 DECODER 绑定、无数组、无共享。
    /// ffmpeg 硬解纹理是 D3D11_BIND_DECODER + Texture2DArray，部分驱动在 VP Blt 时拒绝从
    /// 此类纹理采样（CreateVideoProcessorInputView 宽松放行，Blt 才报 E_INVALIDARG）。
    /// 把切片拷到普通纹理后再做 VP 输入，绕过此限制。
    /// </remarks>
    private void EnsureSingleNv12(int texWidth, int texHeight)
    {
        if (_singleNv12Ptr != IntPtr.Zero && _singleNv12Width == texWidth && _singleNv12Height == texHeight)
            return;

        if (_singleNv12Ptr != IntPtr.Zero)
        {
            D3D11Interop.Release(_singleNv12Ptr);
            _singleNv12Ptr = IntPtr.Zero;
        }

        var nv12Desc = new D3D11Texture2DDesc
        {
            Width = (uint)texWidth,
            Height = (uint)texHeight,
            MipLevels = 1,
            ArraySize = 1, // 单张，非数组
            Format = D3D11Interop.FormatNv12,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = 0, // D3D11_USAGE_DEFAULT
            BindFlags = 0, // 无绑定（非 DECODER、非 SRV、非 RTV）
            CPUAccessFlags = 0,
            MiscFlags = 0, // 无共享标志
        };
        _singleNv12Ptr = D3D11Interop.CreateTexture2D(_devicePtr, nv12Desc);
        _singleNv12Width = texWidth;
        _singleNv12Height = texHeight;
    }
}