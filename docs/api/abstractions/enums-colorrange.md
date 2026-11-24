---
title: "ColorRange"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/ColorRange.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ColorRange", "Unspecified", "Full", "Limited"]
lang: "en-US"
---
# ColorRange

Source: [`ColorRange.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/ColorRange.cs)

The value range of the video component (Full or Limited) determines whether offset/scaling compensation is needed before YUV → RGB conversion.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ColorRange : int
```

## Remarks

Limited (TV interval): Y∈ [16,235], Component ∈ [16,240], offset and scaling compensation required before conversion;
Full (PC interval): 0.. 255 Direct use. Each backend should map its own range fields to this enumeration;
If (`Unspecified`) is not specified, the rendering side reverts to default.

## Values

| Value | Description |
| --- | --- |
| `Unspecified` | Not specified. |
| `Full` | Full range（0..255）。 |
| `Limited` | Limited range (TV range). |
