---
title: "struct"
type: "record"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/VideoColorInfo.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["struct"]
lang: "en-US"
---
# struct

Source: [`VideoColorInfo.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/VideoColorInfo.cs)

Color space description of the video frame (propagated by the decoder from container/output format for rendering to select the correct YUV →RGB conversion).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public readonly record struct VideoColorInfo(ColorStandard Standard, ColorRange Range, ColorTransfer Transfer)
```

## Remarks

`Standard` and `Range` are decided
YUV → RGB brightness/color difference matrix and component offset; `Transfer` Identify transmission characteristics.
If neither of the first two is specified (`IsSpecified` is `false`),
The renderer rolls back BT.601-Full (maintaining the default behavior).

## Members

### Unspecified

```csharp
public bool IsSpecified => Standard != ColorStandard.Unspecified || Range != ColorRange.Unspecified;
```

Whether at least one color parameter is specified—the rendering side determines whether to convert according to the specified matrix.
