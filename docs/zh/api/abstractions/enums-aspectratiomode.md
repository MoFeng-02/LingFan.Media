---
title: "AspectRatioMode"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/AspectRatioMode.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AspectRatioMode", "Fill", "Uniform", "UniformToFill"]
lang: "zh-CN"
---
# AspectRatioMode

源码: [`AspectRatioMode.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/AspectRatioMode.cs)

视频宽高比缩放模式。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum AspectRatioMode : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `Fill` | 拉伸填满目标区域（不保持宽高比）。 |
| `Uniform` | 保持宽高比，留黑边。 |
| `UniformToFill` | 保持宽高比，裁剪溢出部分。 |
