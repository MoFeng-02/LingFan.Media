namespace LingFan.Media.Abstractions;

/// <summary>
/// 有理数（分子/分母），用于表示精确的帧率、宽高比等。
/// </summary>
public readonly struct Rational : IEquatable<Rational>
{
    /// <summary>分子。</summary>
    public int Numerator { get; }

    /// <summary>分母。</summary>
    public int Denominator { get; }

    /// <summary>
    /// 初始化 <see cref="Rational"/> 的新实例。
    /// </summary>
    /// <param name="numerator">分子。</param>
    /// <param name="denominator">分母。</param>
    public Rational(int numerator, int denominator)
    {
        if (denominator == 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "分母不能为零（Rational 表示帧率/宽高比，分母为零无数学意义）。");
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>返回 "num/den" 格式的字符串。</summary>
    public override string ToString() => $"{Numerator}/{Denominator}";

    /// <summary>
    /// 转换为 double（分母为零时返回 0，避免除零）。用于 ffmpeg 时间戳换算
    /// （<c>pts * av_q2d(time_base)</c> 与反向 <c>seconds / av_q2d(time_base)</c>）。
    /// </summary>
    public double ToDouble() => Denominator == 0 ? 0.0 : (double)Numerator / Denominator;

    /// <inheritdoc/>
    public bool Equals(Rational other)
        => Numerator == other.Numerator && Denominator == other.Denominator;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Rational other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    /// <summary>相等比较运算符。</summary>
    public static bool operator ==(Rational left, Rational right) => left.Equals(right);

    /// <summary>不等比较运算符。</summary>
    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);
}
