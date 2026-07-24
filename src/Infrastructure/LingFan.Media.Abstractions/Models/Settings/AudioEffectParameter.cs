namespace LingFan.Media.Abstractions;

/// <summary>
/// 强类型音频效果参数。
/// </summary>
public sealed class AudioEffectParameter
{
    /// <summary>参数名（如 "Frequency"、"Gain"）。</summary>
    public string Name { get; }

    /// <summary>参数值。</summary>
    public float Value { get; set; }

    /// <summary>最小值。</summary>
    public float MinValue { get; }

    /// <summary>最大值。</summary>
    public float MaxValue { get; }

    /// <summary>
    /// 初始化 <see cref="AudioEffectParameter"/> 的新实例。
    /// </summary>
    public AudioEffectParameter(string name, float value, float minValue, float maxValue)
    {
        Name = name;
        Value = value;
        MinValue = minValue;
        MaxValue = maxValue;
    }
}
