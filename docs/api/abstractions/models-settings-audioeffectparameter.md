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

强类型音频效果参数。

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

参数名（如 "Frequency"、"Gain"）。

### Value

```csharp
public float Value { get; set; }
```

参数值。

### MinValue

```csharp
public float MinValue { get; }
```

最小值。

### MaxValue

```csharp
public float MaxValue { get; }
```

最大值。

### Constructor

```csharp
public AudioEffectParameter(string name, float value, float minValue, float maxValue);
```

初始化 `AudioEffectParameter` 的新实例。

**Parameters**

- `name`: 参数名（如 "Frequency"、"Gain"）。
- `value`: 参数值。
- `minValue`: 最小值。
- `maxValue`: 最大值。
