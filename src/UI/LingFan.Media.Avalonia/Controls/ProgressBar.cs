using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 缓冲进度条控件。显示播放进度和缓冲进度两条。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：全部 config 分类——纯属性数据，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class ProgressBar : TemplatedControl
{
    /// <summary>播放进度 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> ProgressProperty =
        AvaloniaProperty.Register<ProgressBar, float>(nameof(Progress));

    /// <summary>缓冲进度 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> BufferedProgressProperty =
        AvaloniaProperty.Register<ProgressBar, float>(nameof(BufferedProgress));

    /// <summary>播放进度 (0.0~1.0)。</summary>
    public float Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>缓冲进度 (0.0~1.0)。</summary>
    public float BufferedProgress
    {
        get => GetValue(BufferedProgressProperty);
        set => SetValue(BufferedProgressProperty, Math.Clamp(value, 0f, 1f));
    }
}
