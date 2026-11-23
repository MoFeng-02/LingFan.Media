---
title: "MediaState"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaState.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaState", "Idle", "Opening", "Buffering", "Playing", "Paused", "Stopped", "Ended", "Error"]
lang: "zh-CN"
---
# MediaState

源码: [`MediaState.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaState.cs)

媒体播放状态。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum MediaState : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `Idle` | 空闲，尚未打开媒体源。 |
| `Opening` | 正在打开媒体源。 |
| `Buffering` | 正在缓冲，等待数据就绪。 |
| `Playing` | 正在播放。 |
| `Paused` | 已暂停。 |
| `Stopped` | 已停止，可重新打开。 |
| `Ended` | 自然播放结束。 |
| `Error` | 错误状态。 |
