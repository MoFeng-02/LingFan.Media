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
/// <para><b>线程安全</b>：Present（渲染线程）和 Render（Avalonia 渲染线程）需同步访问 _bitmap。
/// 使用 lock 保护位图引用。位图在锁内 Lock/写入/Unlock，Render 在锁内读取引用后锁外绘制。</para>
/// <para><b>帧所有权</b>：Present 接管帧所有权，完成后 Dispose 帧。
/// 旧位图在新帧到达且尺寸变化时 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，pattern matching 匹配 IFrameResource 类型。</para>
/// <para><b>V1 限制</b>：仅支持 BGRA32/RGBA32 像素格式的 SoftwareFrameResource。
/// GPU 资源（D3D11TextureResource 等）回退路径 V2 实现。</para>
/// </remarks>
public sealed class SkiaVideoPresenter : IVideoPresenter
{
    private readonly object _frameLock = new();

    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
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

        // 防御性检查：如果已 Dispose，立即释放帧并返回（避免竞态下创建泄漏位图）
        if (_disposed)
        {
            frame.Dispose();
            return;
        }

        try
        {
            if (frame.Resource is SoftwareFrameResource sw)
            {
                var avFormat = sw.Format switch
                {
                    LingFan.Media.Abstractions.PixelFormat.BGRA32 => global::Avalonia.Platform.PixelFormat.Bgra8888,
                    LingFan.Media.Abstractions.PixelFormat.RGBA32 => global::Avalonia.Platform.PixelFormat.Rgba8888,
                    _ => throw new NotSupportedException(
                        $"像素格式 {sw.Format} 在 Skia UI 模式下暂不支持。V1 仅支持 BGRA32/RGBA32。")
                };

                lock (_frameLock)
                {
                    // 双重检查锁定：防止 Dispose 在锁外检查和锁内执行之间运行
                    if (_disposed)
                        return;

                    // 尺寸变化时重建位图
                    if (_bitmap == null || _bitmapWidth != sw.Width || _bitmapHeight != sw.Height)
                    {
                        _bitmap?.Dispose();
                        _bitmapWidth = sw.Width;
                        _bitmapHeight = sw.Height;
                        _bitmap = new WriteableBitmap(
                            new PixelSize(sw.Width, sw.Height),
                            new Vector(96.0 * _scale, 96.0 * _scale),
                            avFormat);
                    }

                    // 写入像素数据（unsafe: IntPtr → Span<byte> 零拷贝）
                    using (var locked = _bitmap.Lock())
                    {
                        // 防止缓冲区溢出：取源数据和目标帧缓冲区的最小值
                        var frameBufferSize = locked.RowBytes * sw.Height;
                        var copyLength = Math.Min(sw.Data.Length, frameBufferSize);

                        unsafe
                        {
                            var destSpan = new Span<byte>((void*)locked.Address, copyLength);
                            sw.Data.Span.Slice(0, copyLength).CopyTo(destSpan);
                        }
                    }
                }
            }
            else
            {
                throw new NotSupportedException(
                    $"帧资源类型 {frame.Resource.GetType().Name} 在 Skia UI 模式下暂不支持。V1 仅支持 SoftwareFrameResource。");
            }
        }
        finally
        {
            // 无论成功或异常，都 Dispose 输入帧（所有权转移语义）
            frame.Dispose();
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

        WriteableBitmap? bitmap;
        int frameWidth, frameHeight;

        lock (_frameLock)
        {
            bitmap = _bitmap;
            frameWidth = _bitmapWidth;
            frameHeight = _bitmapHeight;
        }

        if (bitmap == null || frameWidth <= 0 || frameHeight <= 0)
            return;

        // 计算目标矩形（基于 AspectRatioMode）
        var destRect = CalculateDestRect(frameWidth, frameHeight, _targetWidth, _targetHeight, AspectRatioMode);
        drawingContext.DrawImage(bitmap, destRect);
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
