---
title: "ColorTransfer"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/ColorTransfer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ColorTransfer", "Unspecified", "Linear", "SdrVideo", "St2084", "Hlg"]
lang: "en-US"
---
# ColorTransfer

Source: [`ColorTransfer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/ColorTransfer.cs)

Optoelectronic transmission characteristics (OETF/EOTF curves) used to indicate whether content is HDR.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ColorTransfer : int
```

## Remarks

Mainly provides standard YUV→RGB for SDR content; HDR content (PQ / HLG) is not currently mapped on this layer.
Each backend should map its own transmission characteristic fields to this enumeration; If (`Unspecified`) is not specified, the rendering side reverts to default.

## Values

| Value | Description |
| --- | --- |
| `Unspecified` | Not specified. |
| `Linear` | Linear. |
| `SdrVideo` | SDR video (BT.709 / BT.601 nominal). |
| `St2084` | ST2084（PQ，HDR10）。 |
| `Hlg` | HLG (mixed logarithmic gamma). |
