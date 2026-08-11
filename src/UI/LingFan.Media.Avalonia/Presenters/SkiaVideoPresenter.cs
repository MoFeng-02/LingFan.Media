using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Skia 视频呈现器（Avalonia UI 层）。IVideoPresenter 的默认实现，复用 Avalonia 渲染管线的 SkiaSharp 实例。
/// </summary>
/// <remarks>
/// <para><b>关键原则</b>：不独立引用 SkiaSharp NuGet 包，通过 Avalonia 的 WriteableBitmap
/// 复用底层 SkiaSharp 渲染实例，避免版本冲突、原生库冲突和 AOT 部署问题。</para>
/// <para><b>线程模型修正（修复不出画/冻屏成因）</b>：</para>
/// <list type="bullet">
/// <item>WriteableBitmap 有线程亲缘性——<b>创建 / Lock 写入 / DrawImage 必须发生在同一线程</b>（Avalonia 渲染线程）。
/// 旧实现在管线线程创建并写入位图、渲染线程 DrawImage，跨线程访问抛 InvalidOperationException 致渲染线程崩溃，
/// 表现为「不出画 + 应用冻死（播几秒音乐就没动静）」。</item>
/// <item>本实现将帧像素拷贝与位图绘制解耦：<see cref="Present"/>（管线线程）仅把帧像素转换/拷贝进线程安全的
/// <c>_staging</c> 缓冲（跨线程只读借用，不碰 WriteableBitmap）；<see cref="Render"/>（渲染线程）在锁定内
/// 于<b>渲染线程</b>创建/更新 WriteableBitmap 并 DrawImage。两者经 <c>_gate</c> 同步，规避跨线程亲缘性。</item>
/// </list>
/// <para><b>异步策略</b>：全部同步（Present/Clear/Resize/Render 纯内存 + 像素拷贝；帧所有权归管线，
/// Present 完成后由管线 ReturnFrame，本类绝不 Dispose 帧）。无伪异步——void 方法体内无 await。</para>
/// <para><b>容错</b>：单帧异常（如 GPU 纹理回读失败）在 Present 内吞掉，绝不向上抛到管线线程击杀播放；
/// 不支持的帧格式跳过该帧。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；YUV→BGRA 用预计算只读 LUT（short[]，类型初始化期构造）。</para>
/// </remarks>
public sealed class SkiaVideoPresenter : IVideoPresenter
{
    private readonly object _gate = new();

    /// <summary>线程安全 staging 缓冲：管线线程写入、渲染线程读取，仅经 <c>_gate</c> 保护。</summary>
    private byte[]? _staging;
    private int _stagingW;
    private int _stagingH;
    /// <summary>是否有尚未被渲染线程取走的新帧。</summary>
    private bool _newFrame;

    /// <summary>WriteableBitmap（仅在渲染线程创建/写入/绘制，规避线程亲缘性）。</summary>
    private WriteableBitmap? _bitmap;
    private int _bitmapW;
    private int _bitmapH;
    private global::Avalonia.Platform.PixelFormat _bitmapFormat = global::Avalonia.Platform.PixelFormat.Bgra8888;

    private int _targetW;
    private int _targetH;
    private float _scale = 1.0f;
    private bool _disposed;

    private readonly ILogger? _logger;

    /// <summary>初始化 Skia 呈现器。可选注入日志器，用于暴露帧转换失败（避免静默白屏）。</summary>
    /// <param name="logger">可选日志器；为 null 时不记录。</param>
    public SkiaVideoPresenter(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>宽高比模式。</summary>
    public AspectRatioMode AspectRatioMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>测试可见：当前 WriteableBitmap（无帧时为 null）。</summary>
    internal WriteableBitmap? DebugBitmap => _bitmap;

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targetW = target.Width;
        _targetH = target.Height;
        _scale = target.Scale;
    }

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
                lock (_gate)
                {
                    EnsureStaging(w, h);
                    unsafe
                    {
                        fixed (byte* p = _staging)
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
                    _stagingW = w;
                    _stagingH = h;
                    _newFrame = true;
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
                    EnsureStaging(w, h);
                    unsafe
                    {
                        fixed (byte* p = _staging)
                        {
                            // rb.Data 为 BGRA（最简播放 FrameDumper 的 ReorderBgraToRgba 是 PNG(RGBA) 专用）；
                            // 此处直接整块拷进 staging（BGRA），匹配 Bgra8888 位图，无需重排。
                            rb.Data.Span.CopyTo(new Span<byte>(p, w * h * 4));
                        }
                    }
                    _stagingW = w;
                    _stagingH = h;
                    _newFrame = true;
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

    /// <summary>确保 staging 缓冲足够容纳 w*h*4 字节（BGRA）。</summary>
    private void EnsureStaging(int w, int h)
    {
        int need = w * h * 4;
        if (_staging is null || _staging.Length < need)
            _staging = new byte[need];
    }

    /// <inheritdoc/>
    public void Render(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_disposed)
            return;

        // 仅在渲染线程内：锁定取走新帧像素 → 于本线程创建/更新 WriteableBitmap → 解锁后绘制。
        lock (_gate)
        {
            if (_newFrame && _staging is not null && _stagingW > 0 && _stagingH > 0)
            {
                EnsureBitmap(_stagingW, _stagingH, _bitmapFormat);
                if (_bitmap is not null)
                {
                    using (var locked = _bitmap.Lock())
                    {
                        // staging 为紧凑 BGRA（stride = w*4），Bgra8888 位图 RowBytes 亦为 w*4 → 整块拷贝。
                        System.Runtime.InteropServices.Marshal.Copy(_staging, 0, locked.Address, _stagingW * _stagingH * 4);
                    }
                }
                _newFrame = false;
            }
        }

        if (_bitmap is not null && _bitmapW > 0 && _bitmapH > 0)
        {
            var destRect = CalculateDestRect(_bitmapW, _bitmapH, _targetW, _targetH, AspectRatioMode);
            drawingContext.DrawImage(_bitmap, destRect);
        }
    }

    /// <summary>
    /// 写入打包 4 字节像素（BGRA32/RGBA32），按行拷贝并处理源 stride 对齐填充。
    /// </summary>
    private static unsafe void WriteBgra(ReadOnlySpan<byte> src, int width, int height, int srcStride, IntPtr dest, int destStride)
    {
        int srcStride2 = srcStride > 0 ? srcStride : destStride;
        byte* d = (byte*)dest;

        if (srcStride2 == destStride)
        {
            var copyLength = Math.Min(src.Length, destStride * height);
            src.Slice(0, copyLength).CopyTo(new Span<byte>(d, copyLength));
        }
        else
        {
            var rowBytes = Math.Min(srcStride2, destStride);
            for (int y = 0; y < height; y++)
            {
                int srcOffset = y * srcStride2;
                int available = src.Length - srcOffset;
                if (available <= 0) break;
                int n = Math.Min(rowBytes, available);
                src.Slice(srcOffset, n).CopyTo(new Span<byte>(d + (nuint)(y * destStride), n));
            }
        }
    }

    /// <summary>
    /// 确保 WriteableBitmap 存在且尺寸/格式匹配（不匹配则重建）。始终在渲染线程调用。
    /// </summary>
    private void EnsureBitmap(int width, int height, global::Avalonia.Platform.PixelFormat format)
    {
        if (_bitmap is null || _bitmapW != width || _bitmapH != height || _bitmapFormat != format)
        {
            _bitmap?.Dispose();
            _bitmapW = width;
            _bitmapH = height;
            _bitmapFormat = format;
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96.0 * (_scale > 0 ? _scale : 1.0f), 96.0 * (_scale > 0 ? _scale : 1.0f)),
                format);
        }
    }

    /// <summary>
    /// RGB24（打包 R,G,B 各 1 字节）→ BGRA32。AOT 友好，纯 Span 逐像素写。
    /// </summary>
    internal static unsafe void WriteRgb24ToBgra(SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        int srcStride = sw.Stride > 0 ? sw.Stride : sw.Width * 3;
        byte* dst = (byte*)dest;
        for (int y = 0; y < sw.Height; y++)
        {
            int srcRow = y * srcStride;
            byte* dstRow = dst + (nuint)(y * destStride);
            for (int x = 0; x < sw.Width; x++)
            {
                int s = srcRow + x * 3;
                byte r = sw.Data.Span[s], g = sw.Data.Span[s + 1], b = sw.Data.Span[s + 2];
                int di = x * 4;
                dstRow[di] = b;
                dstRow[di + 1] = g;
                dstRow[di + 2] = r;
                dstRow[di + 3] = 255;
            }
        }
    }

    // ── YUV → BGRA 转换（BT.601 全范围 JFIF 矩阵，预计算 LUT）──

    private static readonly short[] Rv = BuildYuvLut(d => 1.402f * d);
    private static readonly short[] Gu = BuildYuvLut(d => -0.344136f * d);
    private static readonly short[] Gv = BuildYuvLut(d => -0.714136f * d);
    private static readonly short[] Bu = BuildYuvLut(d => 1.772f * d);

    private static short[] BuildYuvLut(Func<float, float> coeff)
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = (int)Math.Round(coeff(i - 128));
            t[i] = v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : (short)v;
        }
        return t;
    }

    /// <summary>
    /// YUV 平面/半平面（YUV420P/YUV422P/YUV444P/NV12/NV21）→ BGRA32。
    /// 假设紧凑平面布局（解码器 av_image_copy_to_buffer align=1 产出）：平面无行内填充、连续排列，
    /// 平面偏移仅由宽高与色度子采样推导（与 av_image_copy_to_buffer 的打包语义一致）。
    /// </summary>
    internal static unsafe void WriteYuvToBgra(SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        byte* dstBase = (byte*)dest;
        int w = sw.Width, h = sw.Height;
        bool isNv = sw.Format is LingFan.Media.Abstractions.PixelFormat.NV12 or LingFan.Media.Abstractions.PixelFormat.NV21;

        int chromaW, chromaH;
        bool hSub, vSub;
        switch (sw.Format)
        {
            case LingFan.Media.Abstractions.PixelFormat.YUV444P:
                chromaW = w; chromaH = h; hSub = false; vSub = false; break;
            case LingFan.Media.Abstractions.PixelFormat.YUV422P:
                chromaW = (w + 1) / 2; chromaH = h; hSub = true; vSub = false; break;
            case LingFan.Media.Abstractions.PixelFormat.NV12:
            case LingFan.Media.Abstractions.PixelFormat.NV21:
                chromaW = w; chromaH = (h + 1) / 2; hSub = false; vSub = true; break;
            default: // YUV420P
                chromaW = (w + 1) / 2; chromaH = (h + 1) / 2; hSub = true; vSub = true; break;
        }

        int ySize = w * h;
        int uOff = ySize;
        int vOff = ySize + chromaW * chromaH;
        int uvOff = ySize;

        for (int y = 0; y < h; y++)
        {
            byte* dstRow = dstBase + (nuint)(y * destStride);
            int yBase = y * w;
            int cRow = vSub ? (y >> 1) : y;
            int uvRowBase = uvOff + cRow * (w * 2);
            int uRowBase = uOff + cRow * chromaW;
            int vRowBase = vOff + cRow * chromaW;

            for (int x = 0; x < w; x++)
            {
                int yv = sw.Data.Span[yBase + x];
                int cu, cv;
                if (isNv)
                {
                    int idx = uvRowBase + x * 2;
                    if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                    {
                        cu = sw.Data.Span[idx];
                        cv = sw.Data.Span[idx + 1];
                    }
                    else // NV21: V 在前
                    {
                        cv = sw.Data.Span[idx];
                        cu = sw.Data.Span[idx + 1];
                    }
                }
                else
                {
                    int cCol = hSub ? (x >> 1) : x;
                    cu = sw.Data.Span[uRowBase + cCol];
                    cv = sw.Data.Span[vRowBase + cCol];
                }

                int r = yv + Rv[cv];
                int g = yv + Gu[cu] + Gv[cv];
                int b = yv + Bu[cu];
                r = r < 0 ? 0 : r > 255 ? 255 : r;
                g = g < 0 ? 0 : g > 255 ? 255 : g;
                b = b < 0 ? 0 : b > 255 ? 255 : b;

                int d = x * 4;
                dstRow[d] = (byte)b;
                dstRow[d + 1] = (byte)g;
                dstRow[d + 2] = (byte)r;
                dstRow[d + 3] = 255;
            }
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }

    /// <inheritdoc/>
    public void Resize(int width, int height, float scale)
    {
        _targetW = width;
        _targetH = height;
        _scale = scale;
    }

    /// <summary>
    /// 根据宽高比模式计算目标矩形。
    /// </summary>
    private static Rect CalculateDestRect(
        int frameWidth, int frameHeight,
        int targetWidth, int targetHeight,
        AspectRatioMode mode)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return new Rect(0, 0, targetWidth, targetHeight);

        var scaleX = (double)targetWidth / frameWidth;
        var scaleY = (double)targetHeight / frameHeight;

        return mode switch
        {
            AspectRatioMode.Fill => new Rect(0, 0, targetWidth, targetHeight),
            AspectRatioMode.Uniform => CalculateUniformRect(scaleX, scaleY, targetWidth, targetHeight),
            AspectRatioMode.UniformToFill => CalculateUniformToFillRect(scaleX, scaleY, targetWidth, targetHeight),
            _ => new Rect(0, 0, targetWidth, targetHeight)
        };
    }

    /// <summary>
    /// Uniform: 保持宽高比，缩放到完全包含在目标内，居中（留黑边）。
    /// </summary>
    private static Rect CalculateUniformRect(double scaleX, double scaleY, int targetWidth, int targetHeight)
    {
        var scale = Math.Min(scaleX, scaleY);
        var destW = targetWidth / scaleX * scale;
        var destH = targetHeight / scaleY * scale;
        var destX = (targetWidth - destW) / 2.0;
        var destY = (targetHeight - destH) / 2.0;
        return new Rect(destX, destY, destW, destH);
    }

    /// <summary>
    /// UniformToFill: 保持宽高比，缩放到完全填充目标，居中（裁剪溢出）。
    /// </summary>
    private static Rect CalculateUniformToFillRect(double scaleX, double scaleY, int targetWidth, int targetHeight)
    {
        var scale = Math.Max(scaleX, scaleY);
        var destW = targetWidth / scaleX * scale;
        var destH = targetHeight / scaleY * scale;
        var destX = (targetWidth - destW) / 2.0;
        var destY = (targetHeight - destH) / 2.0;
        return new Rect(destX, destY, destW, destH);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _staging = null;
        }
    }
}
