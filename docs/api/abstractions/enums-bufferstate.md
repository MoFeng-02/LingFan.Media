---
title: "BufferState"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/BufferState.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["BufferState", "Empty", "Buffering", "Ready", "Starved"]
lang: "en-US"
---
# BufferState

Source: [`BufferState.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/BufferState.cs)

缓冲状态。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum BufferState : int
```

## Values

| Value | Description |
| --- | --- |
| `Empty` | 空，尚未开始缓冲。 |
| `Buffering` | 正在缓冲。 |
| `Ready` | 缓冲就绪，可开始播放。 |
| `Starved` | 缓冲不足，需要重新缓冲。 |
