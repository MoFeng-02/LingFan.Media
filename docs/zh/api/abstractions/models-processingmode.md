---
title: "ProcessingMode"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/ProcessingMode.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ProcessingMode", "RealTime", "Fastest"]
lang: "zh-CN"
---
# ProcessingMode

源码: [`ProcessingMode.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/ProcessingMode.cs)

无头 / 服务端处理模式。控制音视频同步与实时节流行为。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum ProcessingMode
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `RealTime` | 实时模式（默认）：主时钟以音频为基准按真实节奏推进，视频帧经同步器判 Present / Wait / Drop。用于无头预览、或需跟外部时钟对齐的场景。 |
| `Fastest` | 最快模式：关掉音视频同步（所有视频帧直接放行），无头音频输出不做实时节流 （瞬时提交、尽快处理完）。用于转码、离线 ML 推理等批量处理、越快越好的场景。 |
