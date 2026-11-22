using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

// SkiaVideoPresenter 的帧转换职责段（部分类，按职责拆分，编译期等价）。
// 软帧 YUV→BGRA / GPU 纹理回读转 staging（先判定后转换、保鲜窗口跳帧）；Render 绘制在主文件。
public sealed partial class SkiaVideoPresenter
{
    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed)
            return;

        try
        {
            if (frame.Resource is SoftwareFrameResource sw)
            {
                int w = sw.Width, h = sw.Height;
                bool fpOn = FpDue(_fpSeq);
                string fpSrc = fpOn ? FpYuv(sw.Data.Span, w, h) : string.Empty;
                lock (_gate)
                {
                    // 【先判定、后转换】上一帧还没被渲染线程取走时，本帧转换完几乎必然被顶掉
                    // （实测大量帧未渲染即被顶掉，即一半以上帧从未上过屏，
                    //  而每次转换耗时可观，全白花在管线线程上，还会挤压解码喂入的节拍）。
                    // 与其转换再丢弃，不如直接跳过本帧、留住已经转换好的那一帧：
                    // 视觉效果等价（反正两帧里只有一帧会上屏，最多相差一个帧距的延迟），
                    // 但能省下整次 YUV→BGRA。超过保鲜窗口则强制刷新，避免渲染线程卡住时定格。
                    if (_pending is not null && _pendingTicks != 0
                        && (Stopwatch.GetTimestamp() - _pendingTicks) < PendingFreshWindowTicks)
                    {
                        _framesSkipped++;
                        return;
                    }

                    byte[] buf = RentBuffer(w * h * 4);
                    long convStart = Stopwatch.GetTimestamp();
                    unsafe
                    {
                        fixed (byte* p = buf)
                        {
                            IntPtr dest = (IntPtr)p;
                            int destStride = w * 4; // staging 始终紧凑 BGRA
                            switch (sw.Format)
                            {
                                case LingFan.Media.Abstractions.PixelFormat.BGRA32:
                                case LingFan.Media.Abstractions.PixelFormat.RGBA32:
                                    WriteBgra(sw.Data.Span, w, h, sw.Stride, dest, destStride);
                                    break;
                                case LingFan.Media.Abstractions.PixelFormat.RGB24:
                                    WriteRgb24ToBgra(sw, dest, destStride);
                                    break;
                                default:
                                    WriteYuvToBgra(sw, dest, destStride);
                                    break;
                            }
                        }
                    }

                    if (fpOn)
                    {
                        // FP-SRC 与 FP-STG 同源同序号：前者是管线收到的源帧字节，后者是本帧写出的
                        // staging 字节。与解码侧 [FP-DEC] 同 PTS 比对即可判定数据是否在途中被覆写。
                        _logger?.LogInformation(
                            "[FP-PRESENT] seq={Seq} pts={Pts} fmt={Fmt} {W}x{H} src={Src} stg={Stg}",
                            _fpSeq, frame.Timestamp, sw.Format, w, h, fpSrc,
                            FpBgra(buf, w, h));
                    }
                    _fpSeq++;

                    // 帧水印：烧进 staging 左上角（行 12~296，不影响第 500 行的指纹采样点）。
                    if (FrameWatermark)
                    {
                        DrawFrameWatermark(buf, w, h, _presentSeq);
                        if ((_presentSeq % 60) == 0)
                            _logger?.LogInformation(
                                "[WM] 帧水印已烧入 seq={Seq} pts={Pts}（画面左上角 4 位序号；连续⇒内容错，跳号/回跳⇒时序错）",
                                _presentSeq, frame.Timestamp);
                    }
                    _presentSeq++;

                    // 转换完成后才把缓冲交给渲染线程；渲染线程拷贝完毕后才会归还 _free。
                    // 若上一帧还没被取走（渲染线程落后），直接回收复用 —— 等价于丢帧（跳一帧），
                    // 但绝不会覆写渲染线程正在读取的缓冲，而这正是旧双缓冲半帧撕裂的成因。
                    if (_pending is not null)
                    {
                        _framesOverwritten++;
                        ReturnBuffer(_pending);
                    }
                    _pending = buf;
                    _pendingW = w;
                    _pendingH = h;
                    _pendingTicks = Stopwatch.GetTimestamp();

                    // 分体计时：仅 YUV→BGRA 转换本身（不含锁/拷贝外的渲染开销）。
                    _convertSamples++;
                    _convertTicks += Stopwatch.GetElapsedTime(convStart).Ticks;
                    if (_convertSamples >= ConvertLogInterval)
                    {
                        double avgMs = Stopwatch.GetElapsedTime(0, _convertTicks).TotalMilliseconds / _convertSamples;
                        _logger?.LogInformation(
                            "[SKIA-CONVERT] YUV→BGRA 转换性能: 样本={N} 平均={AvgMs:F2}ms/帧 格式={Fmt} 尺寸={W}x{H}",
                            _convertSamples, avgMs, sw.Format, w, h);
                        _convertSamples = 0;
                        _convertTicks = 0;
                    }
                }
            }
            else if (frame.Resource is IGpuTextureResource gpu)
            {
                // GPU 纹理回退：经中立 IGpuTextureResource 桥回读为 BGRA（与 Avalonia Bgra8888 位图一致）。
                // 回读在管线线程执行（与最简播放 GPU 工作同线程），仅把结果像素拷进 staging；
                // 位图本身仍在渲染线程创建/绘制。
                using var rb = gpu.ReadbackToCpu();
                int w = rb.Width, h = rb.Height;
                lock (_gate)
                {
                    byte[] buf = RentBuffer(w * h * 4);
                    unsafe
                    {
                        fixed (byte* p = buf)
                        {
                            // rb.Data 为 BGRA（最简播放 FrameDumper 的 ReorderBgraToRgba 是 PNG(RGBA) 专用）；
                            // 此处直接整块拷进 staging（BGRA），匹配 Bgra8888 位图，无需重排。
                            rb.Data.Span.CopyTo(new Span<byte>(p, w * h * 4));
                        }
                    }

                    // 与软件帧分支同规则：未取走的旧帧回收复用（丢帧），绝不覆写渲染线程持有的缓冲。
                    if (_pending is not null)
                    {
                        _framesOverwritten++;
                        ReturnBuffer(_pending);
                    }
                    _pending = buf;
                    _pendingW = w;
                    _pendingH = h;
                }
            }
            // 其它资源类型：跳过该帧（不抛，避免击杀管线），但必须记录——静默跳过是此前白屏的元凶。
            else
            {
                _logger?.LogWarning(
                    "SkiaVideoPresenter 收到不支持的帧资源类型 {ResourceType}，跳过该帧（视频将不出画）。" +
                    "如需支持该类型，请在 Present 中补充其像素拷贝/回读分支。",
                    frame.Resource?.GetType().Name ?? "<null>");
            }
        }
        catch (Exception ex)
        {
            // 单帧异常（如 GPU 回读失败）记录但不向上抛到管线线程击杀播放——此前 ReadbackToCpu 抛 NotSupported 即被此处吞掉导致白屏。
            _logger?.LogWarning(ex, "SkiaVideoPresenter 帧转位图失败（跳过本帧）。");
        }
    }
}
