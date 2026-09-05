using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LingFan.Media.Avalonia;

/// <summary>
/// MediaState → 颜色画刷值转换器。
/// Playing → 绿色，Paused → 黄色，Error → 红色，其他 → 灰色。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：sync 分类——纯内存颜色映射，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class MediaStateToBrushConverter : IValueConverter
{
    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaState state)
        {
            return state switch
            {
                MediaState.Playing => new SolidColorBrush(Colors.Green),
                MediaState.Paused => new SolidColorBrush(Colors.Gold),
                MediaState.Error => new SolidColorBrush(Colors.Red),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
