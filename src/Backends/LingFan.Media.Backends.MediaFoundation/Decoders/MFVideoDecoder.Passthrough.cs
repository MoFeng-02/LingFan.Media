using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.GPUShare.D3D11;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

// MFVideoDecoder 的直通帧职责段（部分类，按职责拆分，编译期等价）。
// SourceReader 已解码帧的 GPU 纹理零拷贝直通与 NV12 CPU 直通（去 stride 紧凑化）；
// MFT 解码编排在主文件。
internal sealed partial class MFVideoDecoder
{
    /// <summary>
    /// 【A 方案】尝试把 <see cref="MFDemuxer"/> 的「已解码直通包」直接包成 <see cref="VideoFrame"/>，跳过本类 MFT。
    /// </summary>
    /// <param name="packet">待判定的包。</param>
    /// <returns>直通帧；非直通包（压缩裸流）返回 <see langword="null"/>，由调用方走原 MFT 路径。</returns>
    /// <remarks>
    /// <para><b>路径(1)（目标·零拷贝）</b>：<see cref="MediaPacket.HasDecodedFrameResource"/> 为真 ⇒ demuxer 已从
    /// <c>IMFDXGIBuffer</c> 取到 <c>ID3D11Texture2D</c>。此处仅调用 <see cref="MediaPacket.TakeDecodedFrameResource"/>
    /// <b>移交所有权</b>给 <see cref="VideoFrame"/>，全程无一字节内存拷贝。
    /// 必须 Take 而非读 <see cref="MediaPacket.DecodedFrameResource"/>：否则 packet 释放时会销毁仍在渲染管线中的纹理。</para>
    /// <para><b>路径(2)（半 DXVA 回落）</b>：无 GPU 资源但带 <c>Width/Height</c> + NV12 字节 ⇒ 去 stride 紧凑化后
    /// 包成 <see cref="SoftwareFrameResource"/>。仍比改造前好：省掉 MFT 的二次解码。</para>
    /// <para><b>硬解自报</b>：仅在拿到 <see cref="IGpuTextureResource"/> 时才把 <see cref="IsHardwareAccelerated"/>
    /// 置真——CPU 直通帧无法证明其是否由硬件解出（驱动可能内部读回），遵守「S_OK≠被接受，须行为副作用双判据」。</para>
    /// </remarks>
    private VideoFrame? TryBuildPassthroughFrame(MediaPacket packet)
    {
        // 路径(1)：GPU 纹理零拷贝直通
        if (packet.HasDecodedFrameResource)
        {
            var resource = packet.TakeDecodedFrameResource();
            if (resource is not null)
            {
                int w = packet.Width > 0 ? packet.Width : _width;
                int h = packet.Height > 0 ? packet.Height : _height;
                if (w <= 0 || h <= 0)
                {
                    // 尺寸不明无法构帧：资源已被 Take 走，必须由本方法负责释放，绝不泄漏纹理。
                    resource.Dispose();
                    if (!_loggedPassthroughLayoutWarnOnce)
                    {
                        _loggedPassthroughLayoutWarnOnce = true;
                        _logger.LogWarning("[MF-PASSTHRU] 直通 GPU 帧尺寸未知（{W}x{H}），已释放纹理并丢弃该帧", w, h);
                    }
                    return null;
                }

                Interlocked.Increment(ref _passthroughGpuFrames);
                if (resource is IGpuTextureResource)
                    IsHardwareAccelerated = true;   // 拿到显存纹理才算硬解生效

                if (!_loggedPassthroughOnce)
                {
                    _loggedPassthroughOnce = true;
                    _logger.LogInformation(
                        "[MF-PASSTHRU] 解码器直通模式：SourceReader 已硬解出 GPU 纹理 {W}x{H} NV12，MFT 完全旁路 —— 全程零拷贝",
                        w, h);
                }
                return new VideoFrame(w, h, PixelFormat.NV12, resource,
                    packet.Timestamp, packet.Duration, packet.KeyFrame);
            }
        }

        // 路径(2)：NV12 CPU 直通（半 DXVA 回落）
        // 判据：带解码尺寸即为直通包（压缩裸流包的 Width/Height 恒为 0，见 MFDemuxer.ExtractPacket）。
        if (packet.Width > 0 && packet.Height > 0 && packet.Data.Length > 0)
            return BuildCpuPassthroughFrame(packet);

        return null;
    }

    /// <summary>把直通包中的 NV12 字节去 stride 紧凑化，包成 <see cref="VideoFrame"/>。</summary>
    /// <remarks>
    /// <para><b>NV12 源布局</b>：Y 平面 <c>stride × codedH</c> 行 + UV 交错平面 <c>stride × (codedH/2)</c> 行。
    /// 目标为紧凑布局（stride == width），与本类软解路径产出的帧格式完全一致，下游渲染器无需分支。</para>
    /// <para><b>codedH 反推</b>：MF 输出 buffer 的行数按【编码高】（宏块 16 对齐）计，可能 &gt; FRAME_SIZE 的高；
    /// 若按 display 高算 UV 平面偏移会整体错位（画面下半段出现色度错行/绿边）。故用
    /// <c>totalRows = len/stride</c>、<c>codedH = totalRows*2/3</c> 反推真实 Y 平面行数。</para>
    /// <para><b>越界即弃</b>：任一平面所需字节超出 buffer 实长，一律打印一次告警并丢帧，
    /// 绝不「尽力拷贝」——错位画面比丢帧更难定位，且违反「不得静默失效」。</para>
    /// </remarks>
    private VideoFrame? BuildCpuPassthroughFrame(MediaPacket packet)
    {
        int width = packet.Width;
        int height = packet.Height;
        int stride = packet.Stride > 0 ? packet.Stride : width;
        var src = packet.Data.Span;

        int totalRows = src.Length / stride;
        int codedHeight = totalRows * 2 / 3;
        if (codedHeight < height) codedHeight = height;   // 反推失真时退回 display 高，由下方越界检查兜底

        int uvRows = height / 2;
        long needY = (long)(height - 1) * stride + width;
        long uvOffset = (long)stride * codedHeight;
        long needUv = uvRows > 0 ? uvOffset + (long)(uvRows - 1) * stride + width : uvOffset;

        if (src.Length < needY || src.Length < needUv)
        {
            if (!_loggedPassthroughLayoutWarnOnce)
            {
                _loggedPassthroughLayoutWarnOnce = true;
                _logger.LogWarning(
                    "[MF-PASSTHRU] NV12 直通布局校验失败：buffer={Len}B 但 display={W}x{H} stride={S} 推导 codedH={CH} " +
                    "需要 Y≥{NeedY}B / UV尾≥{NeedUv}B ⇒ 布局假定破产，丢弃该帧（绝不按错误 stride 拷贝出错位画面）",
                    src.Length, width, height, stride, codedHeight, needY, needUv);
            }
            return null;
        }

        int dstLen = width * height * 3 / 2;
        var resource = new SoftwareFrameResource(width, height, PixelFormat.NV12, dstLen);
        var dst = resource.Data.Span;

        if (stride == width && codedHeight == height)
        {
            // 紧凑源：整块拷贝（最快路径）
            src.Slice(0, dstLen).CopyTo(dst);
        }
        else
        {
            for (int y = 0; y < height; y++)
                src.Slice(y * stride, width).CopyTo(dst.Slice(y * width, width));

            int srcUv = (int)uvOffset;
            int dstUv = width * height;
            for (int y = 0; y < uvRows; y++)
                src.Slice(srcUv + y * stride, width).CopyTo(dst.Slice(dstUv + y * width, width));
        }

        Interlocked.Increment(ref _passthroughCpuFrames);
        if (!_loggedPassthroughOnce)
        {
            _loggedPassthroughOnce = true;
            _logger.LogInformation(
                "[MF-PASSTHRU] 解码器直通模式（CPU）：SourceReader 已解码但样本落在系统内存（半 DXVA）—— " +
                "{W}x{H} NV12 stride={S} codedH={CH}，MFT 旁路但仍有一次内存拷贝",
                width, height, stride, codedHeight);
        }
        return new VideoFrame(width, height, PixelFormat.NV12, resource,
            packet.Timestamp, packet.Duration, packet.KeyFrame);
    }
}
