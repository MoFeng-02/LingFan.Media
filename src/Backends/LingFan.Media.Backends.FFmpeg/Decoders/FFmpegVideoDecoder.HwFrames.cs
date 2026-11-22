using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.Models;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegVideoDecoder 的 HwFrames 职责段（部分类，由 FFmpegVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeCore/FlushCore/Reset/Dispose）在主文件；硬解/软帧路径与平台初始化。
internal sealed partial class FFmpegVideoDecoder
{
    /// <summary>
    /// av_frame_clone 引用计数共享原生 buffer，避免整帧拷贝。
    /// </summary>
    /// <returns>零拷贝资源；克隆失败（非引用计数帧/内存不足/异常布局）返回 null 由调用方回退拷贝。</returns>
    private static unsafe SoftwareFrameResource? TryCreateZeroCopyResource(
        AVFrame* avFrame, int width, int height, PixelFormat format)
    {
        if (width <= 0 || height <= 0)
            return null; // 异常尺寸交由拷贝路径统一报错

        // av_frame_clone = av_frame_alloc + av_frame_ref：共享所有 buf（引用计数 +1），不拷贝像素
        AVFrame* clone = FF.av_frame_clone(avFrame);
        if (clone == null)
            return null;

        var owner = new SafeAVFrameHandle((IntPtr)clone);

        int stride = clone->linesize[0];
        if (stride <= 0 || clone->data[0] == IntPtr.Zero)
        {
            // 负 stride（自底向上布局）或空数据：不支持零拷贝，释放克隆回退
            owner.Dispose();
            return null;
        }

        // packed 4 字节格式：精确长度 = stride*(height-1) + 行有效载荷，绝不越界读取
        int rowPayload = width * 4;
        int length = stride * (height - 1) + rowPayload;
        var memory = new NativeBufferMemoryManager((IntPtr)clone->data[0], length).Memory;
        return new SoftwareFrameResource(width, height, format, memory, stride, owner);
    }

    /// <summary>
    /// 从 D3D11VA 硬解输出的 AVFrame 创建 VideoFrame（零拷贝 GPU 纹理路径）。
    /// </summary>
    /// <remarks>
    /// <para>D3D11VA 帧布局：data[0] = ID3D11Texture2D*（纹理数组），data[1] = 纹理数组索引。</para>
    /// <para>输出 PixelFormat.NV12（D3D11VA 标准输出格式）。</para>
    /// <para><b>切片保活</b>：必须 <c>av_frame_clone</c> 持有 <c>buf[0]</c> 引用，
    /// 否则调用方 <c>DecodeCore</c> 的 <c>finally { av_frame_free }</c> 会让切片立即回池，
    /// 解码器随后把新图像写进同一切片 ⇒ 渲染时拷到错帧（画面抽帧后跳场景）。
    /// 与软解 BGRA 零拷贝路径、MediaCodec 表面路径保持同一所有权模型。</para>
    /// <para>同步操作（hot 路径）：av_frame_clone（仅引用计数，不拷贝像素）+ COM AddRef + 对象构造，无 I/O。</para>
    /// </remarks>
    private unsafe VideoFrame CreateHardwareFrameFromAVFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;

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

        // GPU 零拷贝：D3D11VA 纹理 → DXGI 共享句柄 → 渲染器生产者（Vulkan/OpenGL）导入为 GPU 纹理上屏。
        if (_gpuImportMode && _gpuProducer is not null)
            return CreateGpuImportFrame(avFrame, width, height, timestamp, duration, keyFrame);

        // D3D11 零拷贝（默认 D3D11 渲染器路径）
        // D3D11VA 帧：data[0] = ID3D11Texture2D*，data[1] = 纹理数组索引
        IntPtr texturePtr = (IntPtr)avFrame->data[0];
        int subresourceIndex = (int)(IntPtr)avFrame->data[1];

        if (texturePtr == IntPtr.Zero)
            throw new InvalidOperationException("D3D11VA 帧纹理指针为空");

        // 切片保活：av_frame_clone = av_frame_alloc + av_frame_ref，对 buf[0]（池内切片）引用计数 +1。
        //    不拷贝任何显存，纯引用计数操作，零拷贝语义不变。
        AVFrame* clone = FF.av_frame_clone(avFrame);
        if (clone == null)
            throw new InvalidOperationException("av_frame_clone 失败（D3D11VA 硬解帧，内存不足）");

        var frameOwner = new SafeAVFrameHandle((IntPtr)clone);

        // D3D11VA 标准输出 NV12 格式（构造失败时由构造函数负责释放 frameOwner，不泄漏）
        var resource = new D3D11HardwareFrameResource(
            texturePtr, width, height, PixelFormat.NV12, subresourceIndex, frameOwner);

        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, PixelFormat.NV12, resource, timestamp, duration: duration, keyFrame);
        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
        return frame;
    }

    /// <summary>
    /// GPU 零拷贝帧创建：把 D3D11VA 硬解纹理经 DXGI 共享句柄导入为渲染器 GPU 纹理（<see cref="IGpuTextureResource"/>）。
    /// </summary>
    /// <remarks>
    /// <para>解码侧先经 GPUShare.D3D11 的 D3D11Nv12ToRgbaConverter 把 NV12 硬解帧 GPU 转 RGBA32
    /// （VideoProcessorBlt，无 CPU 回读），再以 DXGI 共享 NT 句柄交给渲染器生产者导入；三渲染器统一收 RGBA 走零拷贝。
    /// 单纹理、ArrayLayers=1（转换器产出即 RGBA 单平面）。</para>
    /// <para>导入失败（扩展不可用 / 句柄无效 / 切片不兼容）→ 回落 CPU 传输（<see cref="TransferHardwareFrameToCpu"/>），
    /// 计入 CPU 拷贝计数，绝不误报「零拷贝已生效」（导入成功与否以行为副作用为准）。</para>
    /// </remarks>
    private unsafe VideoFrame CreateGpuImportFrame(
        AVFrame* avFrame, int width, int height, TimeSpan timestamp, TimeSpan duration, bool keyFrame)
    {
        IntPtr texturePtr = (IntPtr)avFrame->data[0];
        int subresourceIndex = (int)(IntPtr)avFrame->data[1];

        // 解码侧零拷贝统一路径：ffmpeg NV12 硬解帧经 VideoProcessorBlt 转 RGBA32，
        // 导出共享 BGRA 句柄由生产者（Vulkan/GL/D3D11）导入零拷贝（全程 GPU 内、无 CPU 回读）。
        if (texturePtr != IntPtr.Zero && _nv12ToRgbaConverter is not null)
        {
            try
            {
                if (_nv12ToRgbaConverter.TryConvert(texturePtr, subresourceIndex, width, height,
                        out var rgbaHandle, out var rgbaTexture, out var convertFailure)
                    && rgbaTexture != IntPtr.Zero)
                {
                    var source = new GpuFrameImportSource
                    {
                        Kind = GpuFrameImportKind.D3D11SharedHandle,
                        Handle = rgbaHandle,
                        Width = width,
                        Height = height,
                        Format = PixelFormat.RGBA32,
                        SubresourceIndex = 0,
                        ArrayLayers = 1,
                    };
                    // 把共享句柄交给生产者：TryImport 调用即把 rgbaHandle 所有权转移给生产者；
                    // 无论导入成功或失败，生产者均负责 CloseHandle，本解码器不关，避免双关。
                    if (_gpuProducer!.TryImport(source, out var tex) && tex is not null)
                    {
                        // 共享引用已转移给渲染器；释放解码侧 RGBA 纹理裸指针（底层资源由共享句柄保活）。
                        Marshal.Release(rgbaTexture);
                        var frame = _framePool?.Rent() ?? new VideoFrame();
                        frame.Reset(width, height, PixelFormat.RGBA32, tex, timestamp, duration, keyFrame);
                        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
                        return frame;
                    }

                    _logger.LogWarning(
                        "GPU 零拷贝导入未接受（S_OK≠被接受：行为副作用未成立），本帧回落 CPU 传输。");
                    Marshal.Release(rgbaTexture);
                }
                else
                {
                    // 仅首帧打印完整异常（失败步骤 + HRESULT），后续帧静默避免刷屏；重播时标志已重置。
                    if (!_nv12ConvertFailureLogged)
                    {
                        _nv12ConvertFailureLogged = true;
                        _logger.LogWarning(convertFailure,
                            "NV12→RGBA GPU 转换未成功（{Step}），本帧回落 CPU 传输。",
                            convertFailure is null ? "未知" : convertFailure.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "GPU 零拷贝转换/导入异常（S_OK≠被接受），本帧回落 CPU 传输。");
            }
        }

        // 回落：硬解帧 → CPU（av_hwframe_transfer_data），计入 CPU 拷贝。
        System.Threading.Interlocked.Increment(ref _cpuFallbackFrames);
        return TransferHardwareFrameToCpu(avFrame);
    }

    /// <summary>
    /// GPU 零拷贝导入失败时的稳健回落：把 D3D11VA 硬解帧经 <c>av_hwframe_transfer_data</c> 传输到 CPU 像素帧。
    /// </summary>
    /// <remarks>同步原生调用（无 I/O），属于 CPU 拷贝路径；NV12 走 CreateCopyResource 拷贝，释放临时帧安全。</remarks>
    private unsafe VideoFrame TransferHardwareFrameToCpu(AVFrame* avFrame)
    {
        AVFrame* sw = FF.av_frame_alloc();
        if (sw == null)
            throw new InvalidOperationException("av_frame_alloc 失败（GPU 零拷贝回落 CPU）");
        // av_hwframe_transfer_data 不拷贝时间戳（dst 为裸分配帧）——必须手动透传源帧 pts，
        // 否则回落帧 pts=0 → 全部被判落后丢弃 → 呈现冻结（GL --hw 回落路径实测）。
        sw->pts = avFrame->pts;
        try
        {
            int ret = FF.av_hwframe_transfer_data(sw, avFrame, 0);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwframe_transfer_data 失败: {GetErrorString(ret)}");
            // 传输后再次对齐：个别 hwcontext 传输实现会重建/触碰 dst 元数据，pts 以源帧为唯一权威（幂等）。
            sw->pts = avFrame->pts;
            return CreateVideoFrameFromAVFrame(sw);
        }
        finally
        {
            FF.av_frame_free(&sw);
        }
    }

    /// <summary>
    /// 从 VAAPI 硬解输出的 AVFrame 创建 VideoFrame（Linux 真实零拷贝 GPU 纹理路径）。
    /// </summary>
    /// <remarks>
    /// <para>VAAPI 帧布局：<c>data[3]</c> = VASurfaceID（uint）。经 <see cref="IVaApiExport"/> 把 VA Surface
    /// 导出为 dma_buf（DRM_PRIME_2 / composed NV12 单 fd 双平面），再经匹配的渲染器生产者
    /// （GL/Vulkan）导入为 GPU 纹理上屏（零 CPU 回读）。</para>
    /// <para>切片保活：av_frame_clone 持有 VA Surface 引用，解码器 DecodeCore 的 finally 会 av_frame_free 原帧；
    /// dma_buf 导出后由 EGL/Vulkan 纹理持有独立引用，surface 回收安全。</para>
    /// <para>无匹配生产者 / 导出或导入失败 → 回落 av_hwframe_transfer_data CPU 传输（仍属硬解，仅非零拷贝），计入 CPU 拷贝。</para>
    /// </remarks>
    private unsafe VideoFrame CreateVaApiFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;

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

        // 未初始化 VAAPI / 无导出能力 → 硬解帧回读 CPU（仍属硬解，仅非零拷贝）
        if (_vaDisplay == nint.Zero || _vaApiExport is null)
            return TransferHardwareFrameToCpu(avFrame);

        uint surfaceId = (uint)(nint)avFrame->data[3]; // VASurfaceID

        // 克隆帧保活 surface（原帧将在 DecodeCore finally 被 av_frame_free）
        AVFrame* clone = FF.av_frame_clone(avFrame);
        if (clone == null)
            return TransferHardwareFrameToCpu(avFrame);

        var frameOwner = new SafeAVFrameHandle((nint)clone);
        try
        {
            // VAAPI 零拷贝会话级锁定：首次导出/导入失败即判定「本环境零拷贝不可用」（如 Xvfb/llvmpipe
            // 无法导入 GPU dma_buf），后续帧直接 CPU 传输——逐帧重试导出+导入只空耗并刷屏。
            if (!_vaZeroCopyUnavailable
                && _vaApiExport.TryExportSurfaceToDmaBuf(_vaDisplay, surfaceId, out var desc) && desc is not null
                && _gpuProducer is not null)
            {
                var source = new GpuFrameImportSource
                {
                    Kind = GpuFrameImportKind.LinuxDmaBufFd,
                    Handle = (nint)desc.ObjectFds![0],
                    Width = width,
                    Height = height,
                    Format = PixelFormat.NV12,
                    DrmModifier = desc.Modifier,
                    DrmFourcc = (int)desc.DrmFourcc,
                    PlaneCount = desc.PlaneOffsets!.Length,
                    PlaneFds = desc.ObjectFds,
                    PlaneOffsets = desc.PlaneOffsets,
                    PlanePitches = desc.PlanePitches,
                    SubresourceIndex = 0,
                    ArrayLayers = 1,
                };
                if (_gpuProducer.TryImport(source, out var tex) && tex is not null)
                {
                    var frame = _framePool?.Rent() ?? new VideoFrame();
                    frame.Reset(width, height, PixelFormat.NV12, tex, timestamp, duration, keyFrame);
                    System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
                    return frame;
                }
                _vaZeroCopyUnavailable = true;
                _logger.LogWarning("VAAPI dma_buf 导入未接受（S_OK≠被接受），本次会话回落 CPU 传输（零拷贝已锁定关闭）。");
            }
            else if (!_vaZeroCopyUnavailable)
            {
                _vaZeroCopyUnavailable = true;
                _logger.LogWarning("VAAPI 表面导出 dma_buf 失败，本次会话回落 CPU 传输（零拷贝已锁定关闭）。");
            }

            // 回落 CPU 传输（硬解帧 → CPU 像素帧）；TransferHardwareFrameToCpu 内部计入 CPU 拷贝。
            return TransferHardwareFrameToCpu(clone);
        }
        catch (Exception ex)
        {
            _vaZeroCopyUnavailable = true;
            _logger.LogWarning(ex, "VAAPI 零拷贝导出/导入异常，本次会话回落 CPU 传输（零拷贝已锁定关闭）。");
            return TransferHardwareFrameToCpu(clone);
        }
        finally
        {
            frameOwner.Dispose(); // 释放克隆帧（av_frame_free）；dma_buf 已由 GL/VK 纹理持有独立引用
        }
    }

    /// <summary>
    /// 从 MediaCodec 表面模式输出的 AVFrame 创建 VideoFrame（零拷贝送显路径）。
    /// </summary>
    /// <remarks>
    /// <para>MediaCodec 表面帧布局：<c>data[3]</c> = <c>AVMediaCodecBuffer*</c>，像素驻留 GPU 不可 CPU 访问。</para>
    /// <para>经 <c>av_frame_clone</c>（引用计数）保活缓冲——外层 DecodeCore 的 av_frame_free 不影响克隆帧。</para>
    /// <para>渲染层匹配 <see cref="MediaCodecFrameResource"/> 后调用其 Render() 送显。</para>
    /// <para>同步操作（hot 路径）：引用计数克隆 + 对象构造，无 I/O。</para>
    /// </remarks>
    private unsafe VideoFrame CreateMediaCodecSurfaceFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;

        // 克隆帧（共享引用计数缓冲）保活 AVMediaCodecBuffer
        AVFrame* clone = FF.av_frame_clone(avFrame);
        if (clone == null)
            throw new InvalidOperationException("av_frame_clone 失败（MediaCodec 表面帧，内存不足）");

        var frameOwner = new SafeAVFrameHandle((IntPtr)clone);
        IntPtr mcBuffer = (IntPtr)clone->data[3]; // AVMediaCodecBuffer*
        if (mcBuffer == IntPtr.Zero)
        {
            frameOwner.Dispose();
            throw new InvalidOperationException("MediaCodec 表面帧 data[3] (AVMediaCodecBuffer) 为空");
        }

        var resource = new MediaCodecFrameResource(mcBuffer, width, height, frameOwner);

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

        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, PixelFormat.NV12, resource, timestamp, duration, keyFrame);
        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
        return frame;
    }
}
