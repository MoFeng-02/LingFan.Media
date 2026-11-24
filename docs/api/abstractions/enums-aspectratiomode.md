---
title: "AspectRatioMode"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/AspectRatioMode.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AspectRatioMode", "Fill", "Uniform", "UniformToFill"]
lang: "en-US"
---
# AspectRatioMode

Source: [`AspectRatioMode.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/AspectRatioMode.cs)

Video aspect ratio zoom mode.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum AspectRatioMode : int
```

## Values

| Value | Description |
| --- | --- |
| `Fill` | Stretch to fill the target area (without maintaining the aspect ratio). |
| `Uniform` | Maintain the aspect ratio and leave black borders. |
| `UniformToFill` | Maintain the aspect ratio and trim the overflowing parts. |
