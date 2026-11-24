---
title: "ColorStandard"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/ColorStandard.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ColorStandard", "Unspecified", "Bt601", "Bt709", "Bt2020"]
lang: "en-US"
---
# ColorStandard

Source: [`ColorStandard.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/ColorStandard.cs)

The video color primary color and luminance matrix standard (ITU-R BT series), used to select the correct brightness/chromatic coefficient during YUV→RGB conversion.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ColorStandard : int
```

## Remarks

Each backend (MediaCodec / FFmpeg, etc.) should map its own color standard fields to this enumeration when decoding,
The rendering side selects the corresponding transformation matrix accordingly. If (`Unspecified`) is not specified, the rendering side reverts the default standard.

## Values

| Value | Description |
| --- | --- |
| `Unspecified` | Not specified. The rendering side rolls back the default standard. |
| `Bt601` | ITU-R BT.601（SDTV）。`Kr=0.299, Kb=0.114`。 |
| `Bt709` | ITU-R BT.709（HDTV）。`Kr=0.2126, Kb=0.0722`。 |
| `Bt2020` | ITU-R BT.2020（UHDTV）。 |
