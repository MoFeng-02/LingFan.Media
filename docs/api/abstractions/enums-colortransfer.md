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

光电传输特性（OETF/EOTF 曲线），用于标识内容是否为 HDR。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ColorTransfer : int
```

## Remarks

主要为 SDR 内容提供标准 YUV→RGB；HDR 内容（PQ / HLG）暂不在本层做色调映射。
各后端应将自身的传输特性字段映射到本枚举；未指定（`Unspecified`）时渲染端回退默认。

## Values

| Value | Description |
| --- | --- |
| `Unspecified` | 未指定。 |
| `Linear` | 线性。 |
| `SdrVideo` | SDR 视频（BT.709 / BT.601 标称）。 |
| `St2084` | ST2084（PQ，HDR10）。 |
| `Hlg` | HLG（混合对数伽马）。 |
