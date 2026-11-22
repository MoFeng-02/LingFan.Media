using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.Models;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegVideoDecoder 的 SwFrames 职责段（部分类，由 FFmpegVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeCore/FlushCore/Reset/Dispose）在主文件；硬解/软帧路径与平台初始化。
internal sealed partial class FFmpegVideoDecoder
{
    /// <summary>从 AVFrame 创建 VideoFrame。</summary>
    /// <remarks>
    /// <para>若 _framePool 可用，从池中 Rent 帧壳并调用 Reset 填充数据，复用 VideoFrame 实例减少 GC。</para>
    /// <para>packed BGRA/RGBA 帧走 av_frame_clone 引用计数路径——
    /// 克隆帧共享原生 buffer（内部对所有 buf 做 av_buffer_ref），SoftwareFrameResource
    /// 直接映射 data[0] 并携带实际 stride，Dispose 时经 SafeAVFrameHandle 释放引用。
    /// YUV/planar 格式渲染层无法直接消费（Skia 仅支持 BGRA/RGBA），保持既有拷贝路径。</para>
    /// </remarks>
    private unsafe VideoFrame CreateVideoFrameFromAVFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;
        AVPixelFormat pixFmt = (AVPixelFormat)avFrame->format;
        PixelFormat format = MapPixelFormatFromFFmpeg(pixFmt);

        // packed 4 字节格式优先零拷贝；失败（非引用计数帧/OOM）回退拷贝
        SoftwareFrameResource? resource = null;
        if (format == PixelFormat.P010)
        {
            // 10-bit 单一收敛点：解码侧把 P010(NV12 布局、10bit 存 16-bit 高位)
            // CPU 解包为 BGRA32，渲染器只消费 BGRA32——与 NV12 软件帧/D3D11 不可绑 SRV
            // 的处理完全一致。无黑屏、无静默降 8-bit；真·GPU 10-bit 零拷贝着色器留作未来增强。
            resource = CreateP010ToBgraResource(avFrame, width, height);
        }
        else if (format == PixelFormat.YUV420P10)
        {
            // 10-bit 平面收敛点：ffmpeg 软解 10-bit HEVC 主流出帧为 yuv420p10le(三平面、
            // 10bit 存 16-bit 高位)。与 P010 完全相同的 BT.601 全范围解包到 BGRA32，
            // 仅平面布局不同。若不处理此分支，默认会误当 8-bit YUV420P 解包 => 画面发绿/错色，
            // 且 present 计数仍 PASS（假阳性）。
            resource = CreateYuv420P10ToBgraResource(avFrame, width, height);
        }
        else if (pixFmt is AVPixelFormat.AV_PIX_FMT_BGRA or AVPixelFormat.AV_PIX_FMT_RGBA)
        {
            resource = TryCreateZeroCopyResource(avFrame, width, height, format);
        }
        resource ??= CreateCopyResource(avFrame, width, height, pixFmt, format);

        // pts 权威链：frame->pts 缺席（NOPTS）时回退 best_effort_timestamp（FFmpeg 解码侧到达时间戳，
        // 与 ffplay 同款兜底）；两者皆缺才置零。hwaccel 个别路径存在 pts 缺席场景，兜底防全帧零时戳。
        long framePts = avFrame->pts != FF.AV_NOPTS_VALUE ? avFrame->pts : avFrame->best_effort_timestamp;
        TimeSpan timestamp = framePts != FF.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(framePts * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        TimeSpan duration = avFrame->duration > 0
            ? TimeSpan.FromTicks((long)(avFrame->duration * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        bool keyFrame = (avFrame->flags & FF.AV_FRAME_FLAG_KEY) != 0;

        // 从池中 Rent 帧壳并 Reset 填充数据，复用 VideoFrame 实例。
        // 用 resource.Format（解包路径下为 BGRA32）而非 format，避免枚举/实际布局不一致。
        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, resource.Format, resource, timestamp, duration, keyFrame);
        System.Threading.Interlocked.Increment(ref _cpuFallbackFrames);
        return frame;
    }

    /// <summary>
    /// 拷贝路径（YUV/planar 及零拷贝回退）：av_image_copy_to_buffer 到 ArrayPool buffer。
    /// </summary>
    private static unsafe SoftwareFrameResource CreateCopyResource(
        AVFrame* avFrame, int width, int height, AVPixelFormat pixFmt, PixelFormat format)
    {
        // 使用 FFmpeg av_image_get_buffer_size 计算所需缓冲区大小
        int bufSize = FF.av_image_get_buffer_size(pixFmt, width, height, 1);
        if (bufSize <= 0)
            throw new InvalidOperationException(
                $"av_image_get_buffer_size 返回 {bufSize}（format={pixFmt}, {width}x{height}）");

        // 使用 ArrayPool 租借内存，减少 GC 压力（60fps 每秒 60 个帧）
        var resource = new SoftwareFrameResource(width, height, format, bufSize);

        byte*[] srcData =
        {
            (byte*)(void*)avFrame->data[0], (byte*)(void*)avFrame->data[1],
            (byte*)(void*)avFrame->data[2], (byte*)(void*)avFrame->data[3]
        };
        int[] srcLinesize =
        {
            avFrame->linesize[0], avFrame->linesize[1],
            avFrame->linesize[2], avFrame->linesize[3]
        };

        // 使用 av_image_copy_to_buffer 正确处理所有像素格式（YUV420P/YUV422P/YUV444P/NV12 等），
        // 避免手动计算色度平面高度导致的非 YUV420 格式数据损坏。
        // Pin Memory<byte> 获取原始指针供 FFmpeg 互操作（using var 确保方法返回前释放 GCHandle）
        using var pin = resource.Data.Pin();
        fixed (byte** pSrc = srcData)
        fixed (int* pLines = srcLinesize)
        {
            FF.av_image_copy_to_buffer(
                (byte*)pin.Pointer, bufSize,
                pSrc, pLines,
                pixFmt, width, height, 1);
        }

        return resource;
    }

    /// <summary>
    /// P010(NV12 布局、10bit 存于 16-bit 高位) CPU 解包为 BGRA32 的单一收敛点。
    /// </summary>
    /// <remarks>
    /// <para>10-bit 值经右移 6 位归一化到 0..1023，再套用与 NV12 GPU 着色器 / Skia CPU 兜底
    /// 完全一致的 BT.601 全范围系数（Rv=1.402, Gu=0.344136, Gv=0.714136, Bu=1.772），
    /// 保证 10-bit 与 8-bit 帧色彩不漂移、无静默降精度。这是「10-bit 上屏」的唯一正确入口；
    /// 真·GPU 10-bit 零拷贝着色器（采 unorm16）留作未来增强。</para>
    /// <para>P010 唯一来源是 FFmpeg 软解（MF 解码器硬编码只产 NV12），故解包只落在解码侧，
    /// 渲染器永远只消费 BGRA32，三渲染器（D3D11/OpenGL/Vulkan）+ Skia 均免改。</para>
    /// </remarks>
    private static unsafe SoftwareFrameResource CreateP010ToBgraResource(
        AVFrame* avFrame, int width, int height)
    {
        bool bigEndian = (AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_P010BE;

        int bgraBufSize = width * height * 4;
        var resource = new SoftwareFrameResource(width, height, PixelFormat.BGRA32, bgraBufSize);

        byte* yPlane = (byte*)(void*)avFrame->data[0];
        byte* uvPlane = (byte*)(void*)avFrame->data[1];
        int yStride = avFrame->linesize[0];
        int uvStride = avFrame->linesize[1];
        if (yPlane == null || uvPlane == null || yStride <= 0 || uvStride <= 0)
            throw new InvalidOperationException("P010 帧平面/步幅无效");

        using var pin = resource.Data.Pin();
        byte* dst = (byte*)pin.Pointer;

        // BT.601 全范围系数（与 NV12 着色器、Skia LUT 一致）
        const double rV = 1.402;
        const double gU = 0.344136;
        const double gV = 0.714136;
        const double bU = 1.772;

        for (int y = 0; y < height; y++)
        {
            byte* yRow = yPlane + (nint)y * yStride;
            byte* uvRow = uvPlane + (nint)(y >> 1) * uvStride;
            byte* dstRow = dst + (nint)y * (width * 4);
            for (int x = 0; x < width; x++)
            {
                int yVal = Read10Bit(yRow, x, bigEndian);
                int uVal = Read10Bit(uvRow, (x >> 1) * 2, bigEndian);
                int vVal = Read10Bit(uvRow, (x >> 1) * 2 + 1, bigEndian);

                double yf = yVal / 1023.0;
                double uf = uVal / 1023.0 - 0.5;
                double vf = vVal / 1023.0 - 0.5;

                double r = yf + rV * vf;
                double g = yf - gU * uf - gV * vf;
                double b = yf + bU * uf;

                int di = x * 4;
                dstRow[di] = (byte)ClampTo8Bit(b);
                dstRow[di + 1] = (byte)ClampTo8Bit(g);
                dstRow[di + 2] = (byte)ClampTo8Bit(r);
                dstRow[di + 3] = 255;
            }
        }

        return resource;
    }

    /// <summary>
    /// YUV420P10(三平面、10bit 存于 16-bit 高位) CPU 解包为 BGRA32 的单一收敛点。
    /// </summary>
    /// <remarks>
    /// <para>与 <see cref="CreateP010ToBgraResource"/> 共用同一套 BT.601 全范围系数与
    /// <see cref="Read10Bit"/>（10bit 右移 6 归一），仅平面布局不同：Y/U/V 各自独立平面，
    /// 色度按 2x2 子采样。ffmpeg 软解 10-bit HEVC 主流出帧即为此布局，故这是「10-bit 上屏」
    /// 真正会被触发的主路径。</para>
    /// <para>渲染器永远只消费 BGRA32（资源 Format 已为 BGRA32），三渲染器 + Skia 均免改。</para>
    /// </remarks>
    private static unsafe SoftwareFrameResource CreateYuv420P10ToBgraResource(
        AVFrame* avFrame, int width, int height)
    {
        bool bigEndian = (AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_YUV420P10BE;

        int bgraBufSize = width * height * 4;
        var resource = new SoftwareFrameResource(width, height, PixelFormat.BGRA32, bgraBufSize);

        byte* yPlane = (byte*)(void*)avFrame->data[0];
        byte* uPlane = (byte*)(void*)avFrame->data[1];
        byte* vPlane = (byte*)(void*)avFrame->data[2];
        int yStride = avFrame->linesize[0];
        int uStride = avFrame->linesize[1];
        int vStride = avFrame->linesize[2];
        if (yPlane == null || uPlane == null || vPlane == null
            || yStride <= 0 || uStride <= 0 || vStride <= 0)
            throw new InvalidOperationException("YUV420P10 帧平面/步幅无效");

        using var pin = resource.Data.Pin();
        byte* dst = (byte*)pin.Pointer;

        // BT.601 全范围系数（与 P010 / NV12 着色器 / Skia LUT 完全一致，色彩不漂移）
        const double rV = 1.402;
        const double gU = 0.344136;
        const double gV = 0.714136;
        const double bU = 1.772;

        for (int y = 0; y < height; y++)
        {
            byte* yRow = yPlane + (nint)y * yStride;
            int cy = y >> 1;
            byte* uRow = uPlane + (nint)cy * uStride;
            byte* vRow = vPlane + (nint)cy * vStride;
            byte* dstRow = dst + (nint)y * (width * 4);
            for (int x = 0; x < width; x++)
            {
                int yVal = Read10Bit(yRow, x, bigEndian);
                int uVal = Read10Bit(uRow, x >> 1, bigEndian);
                int vVal = Read10Bit(vRow, x >> 1, bigEndian);

                double yf = yVal / 1023.0;
                double uf = uVal / 1023.0 - 0.5;
                double vf = vVal / 1023.0 - 0.5;

                double r = yf + rV * vf;
                double g = yf - gU * uf - gV * vf;
                double b = yf + bU * uf;

                int di = x * 4;
                dstRow[di] = (byte)ClampTo8Bit(b);
                dstRow[di + 1] = (byte)ClampTo8Bit(g);
                dstRow[di + 2] = (byte)ClampTo8Bit(r);
                dstRow[di + 3] = 255;
            }
        }

        return resource;
    }

    /// <summary>从 16-bit 半平面读出第 <paramref name="sampleIndex"/> 个样本的 10-bit 值(0..1023)。</summary>
    /// <remarks>
    /// 本机 ffmpeg 将 10-bit 值<b>右对齐</b>存入 16-bit 字（值位于低 10 位，高 6 位填零），
    /// 故直接取低 10 位即可，不要做 <c>raw &gt;&gt; 6</c>（那会把中性色度 512 错读成 8，导致满屏绿）。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int Read10Bit(byte* plane, int sampleIndex, bool bigEndian)
    {
        byte* p = plane + (nint)sampleIndex * 2;
        ushort raw = bigEndian
            ? (ushort)((p[0] << 8) | p[1])
            : (ushort)((p[1] << 8) | p[0]);
        return raw & 0x3FF; // 10-bit 右对齐：值位于低 10 位 => 0..1023
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampTo8Bit(double v)
    {
        int i = (int)(v * 255.0 + 0.5);
        return i < 0 ? 0 : i > 255 ? 255 : i;
    }
}
