---
title: "AudioEffectParameter"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/AudioEffectParameter.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioEffectParameter", "Name", "Value", "MinValue", "MaxValue"]
lang: "zh-CN"
---
# AudioEffectParameter

源码: [`AudioEffectParameter.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/AudioEffectParameter.cs)

强类型音频效果参数。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class AudioEffectParameter
```

## 成员

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

### 构造函数

```csharp
public AudioEffectParameter(string name, float value, float minValue, float maxValue);
```

初始化 `AudioEffectParameter` 的新实例。
