---
title: "struct"
type: "record"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/VideoColorInfo.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["struct"]
lang: "zh-CN"
---
# struct

源码: [`VideoColorInfo.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/VideoColorInfo.cs)

视频帧的色彩空间描述（由解码器从容器/输出格式透传，供渲染端选择正确的 YUV→RGB 转换）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public readonly record struct VideoColorInfo(ColorStandard Standard, ColorRange Range, ColorTransfer Transfer)
```

## 备注

`Standard` 与 `Range` 决定
YUV→RGB 的亮度/色差矩阵与分量偏移；`Transfer` 标识传输特性。
前两者均未指定（`IsSpecified` 为 `false`）时，
渲染端回退 BT.601-Full（保持既有默认行为）。

## 成员

### Unspecified

```csharp
public bool IsSpecified => Standard != ColorStandard.Unspecified || Range != ColorRange.Unspecified;
```

是否指定了至少一项色彩参数——渲染端据此判断是否需按指定矩阵转换。
