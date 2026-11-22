using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

// SkiaVideoPresenter 的 Layout 职责段（部分类，由 SkiaVideoPresenter.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/Present/Render/Clear/Resize/Dispose）在主文件；软帧 CPU 转换与布局计算。
public sealed partial class SkiaVideoPresenter
{
    /// <summary>帧位图绘进 <paramref name="destRect"/> 后的**设备像素**缩放比
    /// （destRect 是 DIP，乘 <c>_scale</c> 才是设备像素）。1.0 表示 1:1 贴块，&lt;1 表示缩小。</summary>
    private double DeviceScaleRatio(Rect destRect)
        => _bitmapW > 0 && destRect.Width > 0 ? (destRect.Width * _scale) / (double)_bitmapW : 1.0;

    /// <summary>按设备缩放比挑选位图插值模式：
    /// 1:1（≥0.995）用 None（最近邻，1:1 时零误差且最快）；
    /// 轻中度缩小（≥0.5）用 LowQuality（双线性，足够且便宜）；
    /// 重度缩小（&lt;0.5）才用 HighQuality（三次插值）抑制摩尔纹与欠采样发糊。</summary>
    private BitmapInterpolationMode ChooseInterpolation(Rect destRect)
    {
        double ratio = DeviceScaleRatio(destRect);
        return ratio >= 0.995 ? BitmapInterpolationMode.None
             : ratio >= 0.5 ? BitmapInterpolationMode.LowQuality
             : BitmapInterpolationMode.HighQuality;
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
}
