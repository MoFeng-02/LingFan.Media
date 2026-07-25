using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace LingFan.Media.Avalonia;

/// <summary>
/// TimeSpan → "mm:ss" / "hh:mm:ss" 值转换器。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：sync 分类——纯内存字符串格式化，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class TimeSpanToStringConverter : IValueConverter
{
    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            return ts.TotalHours >= 1
                ? ts.ToString(@"hh\:mm\:ss")
                : ts.ToString(@"mm\:ss");
        }
        return "00:00";
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str && TimeSpan.TryParse(str, culture, out var ts))
            return ts;
        return BindingOperations.DoNothing;
    }
}
