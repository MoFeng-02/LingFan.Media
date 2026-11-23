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

视频分量的取值区间（Full 或 Limited），决定 YUV→RGB 转换前是否需要偏移/缩放补偿。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ColorRange : int
```

## Remarks

Limited（TV 区间）：Y∈[16,235]、色差∈[16,240]，转换前须做偏移与缩放补偿；
Full（PC 区间）：0..255 直接使用。各后端应将自身范围字段映射到本枚举；
未指定（`Unspecified`）时渲染端回退默认。

## Values

| Value | Description |
| --- | --- |
| `Unspecified` | 未指定。 |
| `Full` | Full range（0..255）。 |
| `Limited` | Limited range（TV 区间）。 |
