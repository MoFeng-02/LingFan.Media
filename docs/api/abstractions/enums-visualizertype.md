---
title: "VisualizerType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/VisualizerType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VisualizerType", "Spectrum", "Waveform", "Bars"]
lang: "en-US"
---
# VisualizerType

Source: [`VisualizerType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/VisualizerType.cs)

Audio visualization type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum VisualizerType : int
```

## Values

| Value | Description |
| --- | --- |
| `Spectrum` | Spectrum (FFT frequency domain analysis). |
| `Waveform` | Waveform (time-domain waveform). |
| `Bars` | Classic columnar spectrum. |
