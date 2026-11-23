---
title: "ClockSyncSource"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/ClockSyncSource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ClockSyncSource", "Audio", "Video", "System"]
lang: "zh-CN"
---
# ClockSyncSource

源码: [`ClockSyncSource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/ClockSyncSource.cs)

时钟同步源。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum ClockSyncSource : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `Audio` | 音频时钟为主（IAudioOutput.PlaybackPosition 驱动）。 |
| `Video` | 视频时钟为主（VideoFrame.Timestamp 驱动）。 |
| `System` | 系统时钟为主（Stopwatch 驱动，无声时降级）。 |
