---
title: "Rational"
type: "struct"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Rational.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["Rational", "Numerator", "Denominator", "ToString", "ToDouble", "Equals", "GetHashCode"]
lang: "zh-CN"
---
# Rational

源码: [`Rational.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Rational.cs)

有理数（分子/分母），用于表示精确的帧率、宽高比等。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public readonly struct Rational : IEquatable<Rational>
```

## 成员

### Numerator

```csharp
public int Numerator { get; }
```

分子。

### Denominator

```csharp
public int Denominator { get; }
```

分母。

### 构造函数

```csharp
public Rational(int numerator, int denominator);
```

初始化 `Rational` 的新实例。

**参数**

- `numerator`：分子。
- `denominator`：分母。

### ToString

```csharp
public override string ToString() => $"{Numerator}/{Denominator}";
```

返回 "num/den" 格式的字符串。

### ToDouble

```csharp
public double ToDouble() => Denominator == 0 ? 0.0 : (double)Numerator / Denominator;
```

转换为 double（分母为零时返回 0，避免除零）。用于 ffmpeg 时间戳换算
（`pts * av_q2d(time_base)` 与反向 `seconds / av_q2d(time_base)`）。

### Equals

```csharp
public bool Equals(Rational other)
    => Numerator == other.Numerator && Denominator == other.Denominator;
```

继承自基契约的文档。

### Equals

```csharp
public override bool Equals(object? obj) => obj is Rational other && Equals(other);
```

继承自基契约的文档。

### GetHashCode

```csharp
public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
```

继承自基契约的文档。

### Equals

```csharp
public static bool operator ==(Rational left, Rational right) => left.Equals(right);
```

相等比较运算符。

### Equals

```csharp
public static bool operator !=(Rational left, Rational right) => !left.Equals(right);
```

不等比较运算符。
