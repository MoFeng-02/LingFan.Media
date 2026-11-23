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

视频的色彩原色与亮度矩阵标准（ITU-R BT 系列），用于 YUV→RGB 转换时选择正确的亮度/色差系数。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ColorStandard : int
```

## Remarks

各后端（MediaCodec / FFmpeg 等）解码时应将自身的色彩标准字段映射到本枚举，
渲染端据此选择相应转换矩阵。未指定（`Unspecified`）时渲染端回退默认标准。

## Values

| Value | Description |
| --- | --- |
| `Unspecified` | 未指定。渲染端回退默认标准。 |
| `Bt601` | ITU-R BT.601（SDTV）。`Kr=0.299, Kb=0.114`。 |
| `Bt709` | ITU-R BT.709（HDTV）。`Kr=0.2126, Kb=0.0722`。 |
| `Bt2020` | ITU-R BT.2020（UHDTV）。 |
