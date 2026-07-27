using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Skia 视频呈现器。IVideoPresenter 的默认实现，复用 Avalonia 渲染管线的 SkiaSharp 实例。
/// </summary>
/// <remarks>
/// <para><b>关键原则</b>：不独立引用 SkiaSharp NuGet 包，通过 Avalonia 的 WriteableBitmap
/// 复用底层 SkiaSharp 渲染实例。WriteableBitmap 是 Avalonia 对 Skia 像素缓冲区的封装，
/// 内部由 SkiaSharp 驱动，避免版本冲突、原生库冲突和 AOT 部署问题。</para>
/// <para><b>异步策略</b>：全部同步（sync / native 分类）——
/// Present/Clear/Resize/Dispose 为 sync（纯内存 + GPU 操作）；
/// Render 为 native（Avalonia Render 覆写，void 签名是框架硬限制）。
/// 绝对无伪异步——所有 void 方法体内无 await，无 .Wait()，无 .Result。</para>
/// <para><b>线程安全（U8 竞态修复）</b>：Present（渲染线程）和 Render（Avalonia 渲染线程）需同步访问 _bitmap。
/// V1 竞态：Render 在锁内捕获 _bitmap 引用后释放锁，DrawImage 在锁外执行，期间 Present/Dispose 可能
/// 释放该位图导致绘制已释放对象。V2 修复：<b>DrawImage 在锁内完成</b>（不释放锁），保证 _bitmap 在绘制期间
/// 不被 Dispose/替换；锁内绘制仅短暂阻塞 Present（视频管线线程），属可接受的背压，且无伪异步。</para>
/// <para><b>帧所有权</b>：V2 变更——Present 不再接管帧所有权，完成后不 Dispose 帧。
/// 调用方（VideoPipeline）负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>像素格式（U11）</b>：V1 仅 BGRA32/RGBA32。V2 新增 YUV420P/YUV422P/YUV444P/NV12/NV21/RGB24
/// → BGRA 的 CPU 转换（Span + LUT，AOT 友好）。转换假设解码器拷贝路径的紧凑平面布局
/// （av_image_copy_to_buffer align=1 产出：平面无行内填充、平面连续排列），故平面偏移仅由
/// 宽高/格式推导，无需帧携带逐平面 stride。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；YUV→RGB 用预计算只读 LUT（short[]，类型初始化期构造）。</para>
/// </remarks>
public sealed class SkiaVideoPresenter : IVideoPresenter
{
    private readonly object _frameLock = new();

    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private global::Avalonia.Platform.PixelFormat _bitmapFormat = global::Avalonia.Platform.PixelFormat.Bgra8888;
    private int _targetWidth;
    private int _targetHeight;
    private float _scale = 1.0f;
    private bool _disposed;

    /// <summary>宽高比模式。</summary>
    public AspectRatioMode AspectRatioMode { get; set; } = AspectRatioMode.Uniform;

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targetWidth = target.Width;
        _targetHeight = target.Height;
        _scale = target.Scale;
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // 防御性检查：如果已 Dispose，直接返回（V2: 调用方负责帧生命周期）
        if (_disposed)
        {
            return;
        }

        if (frame.Resource is SoftwareFrameResource sw)
        {
            // 决定 WriteableBitmap 目标格式：所有非 BGRA 源格式统一转换为 BGRA32 写入
            var avFormat = sw.Format switch
            {
                LingFan.Media.Abstractions.PixelFormat.BGRA32 => global::Avalonia.Platform.PixelFormat.Bgra8888,
                LingFan.Media.Abstractions.PixelFormat.RGBA32 => global::Avalonia.Platform.PixelFormat.Rgba8888,
                LingFan.Media.Abstractions.PixelFormat.RGB24 => global::Avalonia.Platform.PixelFormat.Bgra8888,
                LingFan.Media.Abstractions.PixelFormat.YUV420P => global::Avalonia.Platform.PixelFormat.Bgra8888,
                LingFan.Media.Abstractions.PixelFormat.YUV422P => global::Avalonia.Platform.PixelFormat.Bgra8888,
                LingFan.Media.Abstractions.PixelFormat.YUV444P => global::Avalonia.Platform.PixelFormat.Bgra8888,
                LingFan.Media.Abstractions.PixelFormat.NV12 => global::Avalonia.Platform.PixelFormat.Bgra8888,
                LingFan.Media.Abstractions.PixelFormat.NV21 => global::Avalonia.Platform.PixelFormat.Bgra8888,
                _ => throw new NotSupportedException(
                    $"像素格式 {sw.Format} 在 Skia UI 模式下暂不支持。V2 支持 BGRA32/RGBA32/RGB24/YUV420P/YUV422P/YUV444P/NV12/NV21。")
            };

            lock (_frameLock)
            {
                // 双重检查锁定：防止 Dispose 在锁外检查和锁内执行之间运行
                if (_disposed)
                    return;

                // 尺寸或格式变化时重建位图
                if (_bitmap == null || _bitmapWidth != sw.Width || _bitmapHeight != sw.Height || _bitmapFormat != avFormat)
                {
                    _bitmap?.Dispose();
                    _bitmapWidth = sw.Width;
                    _bitmapHeight = sw.Height;
                    _bitmapFormat = avFormat;
                    _bitmap = new WriteableBitmap(
                        new PixelSize(sw.Width, sw.Height),
                        new Vector(96.0 * _scale, 96.0 * _scale),
                        avFormat);
                }

                // 写入像素数据（unsafe: IntPtr → Span<byte> 零拷贝）
                using (var locked = _bitmap.Lock())
                {
                    var src = sw.Data.Span;
                    IntPtr dest = locked.Address;
                    int destStride = locked.RowBytes;

                    switch (sw.Format)
                    {
                        case LingFan.Media.Abstractions.PixelFormat.BGRA32:
                        case LingFan.Media.Abstractions.PixelFormat.RGBA32:
                            WritePacked(src, sw, dest, destStride);
                            break;
                        case LingFan.Media.Abstractions.PixelFormat.RGB24:
                            WriteRgb24ToBgra(src, sw, dest, destStride);
                            break;
                        default:
                            // YUV420P / YUV422P / YUV444P / NV12 / NV21 → BGRA
                            WriteYuvToBgra(src, sw, dest, destStride);
                            break;
                    }
                }
            }
        }
        else
        {
            throw new NotSupportedException(
                $"帧资源类型 {frame.Resource?.GetType().Name ?? "null"} 在 Skia UI 模式下暂不支持。V1/V2 仅支持 SoftwareFrameResource（GPU 纹理回退 U6 属独立 PR）。");
        }
        // V2: Present 不再 Dispose 帧——调用方（VideoPipeline）负责 Return 到 FramePool
    }

    /// <summary>
    /// 写入打包 4 字节像素（BGRA32/RGBA32），按行拷贝并处理源 stride 对齐填充。
    /// </summary>
    private static unsafe void WritePacked(ReadOnlySpan<byte> src, SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        // V2-05: 零拷贝帧携带原生 stride（可能含对齐填充）；Stride==0 为历史紧凑布局，
        // 视为与目标一致（保持 V1 行为）。
        int srcStride = sw.Stride > 0 ? sw.Stride : destStride;
        byte* d = (byte*)dest;

        if (srcStride == destStride)
        {
            // 快路径：源/目标 stride 一致，整块拷贝
            var copyLength = Math.Min(src.Length, destStride * sw.Height);
            src.Slice(0, copyLength).CopyTo(new Span<byte>(d, copyLength));
        }
        else
        {
            // 慢路径：stride 不一致，逐行拷贝有效载荷
            var rowBytes = Math.Min(srcStride, destStride);
            for (int y = 0; y < sw.Height; y++)
            {
                int srcOffset = y * srcStride;
                int available = src.Length - srcOffset;
                if (available <= 0) break;
                int n = Math.Min(rowBytes, available);
                src.Slice(srcOffset, n).CopyTo(new Span<byte>(d + (nuint)(y * destStride), n));
            }
        }
    }

    /// <summary>
    /// RGB24（打包 R,G,B 各 1 字节）→ BGRA32。AOT 友好，纯 Span 逐像素写。
    /// </summary>
    internal static unsafe void WriteRgb24ToBgra(ReadOnlySpan<byte> src, SoftwareFrameResource sw, IntPtr dest, int destStride)
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
                byte r = src[s], g = src[s + 1], b = src[s + 2];
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
    internal static unsafe void WriteYuvToBgra(ReadOnlySpan<byte> src, SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        byte* dstBase = (byte*)dest;
        int w = sw.Width, h = sw.Height;
        bool isNv = sw.Format is LingFan.Media.Abstractions.PixelFormat.NV12 or LingFan.Media.Abstractions.PixelFormat.NV21;

        // 推导色度平面尺寸（与 av_image_get_linesize / av_image_copy_to_buffer 一致）
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
        int uvOff = ySize; // NV12/NV21 交错色度平面基址

        for (int y = 0; y < h; y++)
        {
            byte* dstRow = dstBase + (nuint)(y * destStride);
            int yBase = y * w;
            int cRow = vSub ? (y >> 1) : y;
            int uvRowBase = uvOff + cRow * (w * 2);   // NV 色度行宽 = w * 2 字节（紧凑）
            int uRowBase = uOff + cRow * chromaW;
            int vRowBase = vOff + cRow * chromaW;

            for (int x = 0; x < w; x++)
            {
                int yv = src[yBase + x];
                int cu, cv;
                if (isNv)
                {
                    int idx = uvRowBase + x * 2;
                    if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                    {
                        cu = src[idx];
                        cv = src[idx + 1];
                    }
                    else // NV21: V 在前
                    {
                        cv = src[idx];
                        cu = src[idx + 1];
                    }
                }
                else
                {
                    int cCol = hSub ? (x >> 1) : x;
                    cu = src[uRowBase + cCol];
                    cv = src[vRowBase + cCol];
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
        lock (_frameLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }

    /// <inheritdoc/>
    public void Resize(int width, int height, float scale)
    {
        _targetWidth = width;
        _targetHeight = height;
        _scale = scale;
    }

    /// <inheritdoc/>
    public void Render(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_disposed)
            return;

        // U8 竞态修复：DrawImage 必须在锁内完成——防止释放锁后 Present/Dispose 释放正在绘制的 _bitmap。
        // 锁内绘制仅短暂阻塞 Present（视频管线线程），是可接受的背压；纯 GPU blit，无 I/O、无 await，非伪异步。
        lock (_frameLock)
        {
            if (_bitmap == null || _bitmapWidth <= 0 || _bitmapHeight <= 0)
                return;

            var destRect = CalculateDestRect(_bitmapWidth, _bitmapHeight, _targetWidth, _targetHeight, AspectRatioMode);
            drawingContext.DrawImage(_bitmap, destRect);
        }
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

        lock (_frameLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
