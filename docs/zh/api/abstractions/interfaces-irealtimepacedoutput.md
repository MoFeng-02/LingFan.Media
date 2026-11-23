---
title: "IRealtimePacedOutput"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/IRealtimePacedOutput.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IRealtimePacedOutput", "PaceRealTime"]
lang: "zh-CN"
---
# IRealtimePacedOutput

源码: [`IRealtimePacedOutput.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/IRealtimePacedOutput.cs)

可实时节流的音频输出标记接口。无头（无硬件）音频输出实现它，
使 `IMediaPlayer` 在 `Fastest` 下关闭实时节流
（瞬时提交、尽快处理完）。
真实硬件输出（WASAPI 等）由硬件节奏限速，无需实现本接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IRealtimePacedOutput
```

## 备注

零外部引用：仅依赖契约层中立类型。

## 成员

### PaceRealTime

```csharp
bool PaceRealTime { set; }
```

是否按真实节奏节流提交：true = 实时（默认）；false = 最快不节流。
