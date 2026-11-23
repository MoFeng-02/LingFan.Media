---
title: "VisualizerType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/VisualizerType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VisualizerType", "Spectrum", "Waveform", "Bars"]
lang: "zh-CN"
---
# VisualizerType

源码: [`VisualizerType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/VisualizerType.cs)

音频可视化类型。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum VisualizerType : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `Spectrum` | 频谱（FFT 频域分析）。 |
| `Waveform` | 波形（时域波形）。 |
| `Bars` | 经典柱状频谱。 |
