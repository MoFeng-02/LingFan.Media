---
title: "AudioEffectParameter"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/AudioEffectParameter.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioEffectParameter", "Name", "Value", "MinValue", "MaxValue"]
lang: "en-US"
---
# AudioEffectParameter

Source: [`AudioEffectParameter.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/AudioEffectParameter.cs)

Strong type audio effect parameters.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class AudioEffectParameter
```

## Members

### Name

```csharp
public string Name { get; }
```

Parameter names (such as "Frequency", "Gain").

### Value

```csharp
public float Value { get; set; }
```

Parameter value.

### MinValue

```csharp
public float MinValue { get; }
```

Minimum value.

### MaxValue

```csharp
public float MaxValue { get; }
```

Maximum value.

### Constructor

```csharp
public AudioEffectParameter(string name, float value, float minValue, float maxValue);
```

Initialize a new instance of `AudioEffectParameter`.

**Parameters**

- `name`: Parameter names (such as "Frequency", "Gain").
- `value`: Parameter value.
- `minValue`: Minimum value.
- `maxValue`: Maximum value.
