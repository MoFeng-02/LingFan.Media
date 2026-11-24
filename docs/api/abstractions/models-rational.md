---
title: "Rational"
type: "struct"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Rational.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["Rational", "Numerator", "Denominator", "ToString", "ToDouble", "Equals", "GetHashCode"]
lang: "en-US"
---
# Rational

Source: [`Rational.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Rational.cs)

Rational numbers (numerator/denominator), used to represent precise frame rates, aspect ratios, etc.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public readonly struct Rational : IEquatable<Rational>
```

## Members

### Numerator

```csharp
public int Numerator { get; }
```

Molecule.

### Denominator

```csharp
public int Denominator { get; }
```

Denominator.

### Constructor

```csharp
public Rational(int numerator, int denominator);
```

Initialize a new instance of `Rational`.

**Parameters**

- `numerator`: Molecule.
- `denominator`: Denominator.

### ToString

```csharp
public override string ToString() => $"{Numerator}/{Denominator}";
```

Returns a string in the format "num/den".

### ToDouble

```csharp
public double ToDouble() => Denominator == 0 ? 0.0 : (double)Numerator / Denominator;
```

Convert to double (returns 0 when the denominator is zero, avoiding division by zero). Used for ffmpeg timestamp conversion
(`pts * av_q2d(time_base)` and reverse `seconds / av_q2d(time_base)`).

### Equals

```csharp
public bool Equals(Rational other)
    => Numerator == other.Numerator && Denominator == other.Denominator;
```

Inherits documentation from its base contract.

### Equals

```csharp
public override bool Equals(object? obj) => obj is Rational other && Equals(other);
```

Inherits documentation from its base contract.

### GetHashCode

```csharp
public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
```

Inherits documentation from its base contract.

### Equals

```csharp
public static bool operator ==(Rational left, Rational right) => left.Equals(right);
```

Equal comparison operator.

### Equals

```csharp
public static bool operator !=(Rational left, Rational right) => !left.Equals(right);
```

Equal comparison operator.
