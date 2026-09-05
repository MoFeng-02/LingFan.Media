using Avalonia;
using Avalonia.Controls.Primitives;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 音量滑块控件。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：全部 config 分类——纯属性数据，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class VolumeSlider : TemplatedControl
{
    /// <summary>音量 (0.0~1.0) 的 StyledProperty。</summary>
    public static readonly StyledProperty<float> VolumeProperty =
        AvaloniaProperty.Register<VolumeSlider, float>(nameof(Volume), defaultValue: 1.0f);

    /// <summary>是否静音的 StyledProperty。</summary>
    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<VolumeSlider, bool>(nameof(IsMuted));

    /// <summary>音量 (0.0~1.0)。</summary>
    public float Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>是否静音。</summary>
    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }
}
