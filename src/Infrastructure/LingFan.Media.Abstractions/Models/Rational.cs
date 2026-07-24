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
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>返回 "num/den" 格式的字符串。</summary>
    public override string ToString() => $"{Numerator}/{Denominator}";

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
