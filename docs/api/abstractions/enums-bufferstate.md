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

Buffer state.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum BufferState : int
```

## Values

| Value | Description |
| --- | --- |
| `Empty` | Empty, not yet starting to buffer. |
| `Buffering` | Buffering. |
| `Ready` | Buffering is ready, playback can begin. |
| `Starved` | Buffering is insufficient and needs to be re-buffered. |
