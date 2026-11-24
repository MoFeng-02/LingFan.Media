---
title: "IBufferManager"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IBufferManager.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IBufferManager", "BufferedDuration", "BufferedBytes", "IsReady", "State", "TargetDuration", "BufferProgressChanged", "StartAsync", "Stop", "Clear"]
lang: "en-US"
---
# IBufferManager

Source: [`IBufferManager.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IBufferManager.cs)

Buffer manager interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IBufferManager
```

## Remarks

Manage packet buffering between Demuxer and Decoder.

FrameBuffer No SubtitleFrameQueue — Subtitle frames contain only text and timestamps,

No GPU resources, no Dispose, no real-time requirements. Subtitle frames are managed by the internal cache of the SubtitleProcessor.

## Members

### BufferedDuration

```csharp
TimeSpan BufferedDuration { get; }
```

Current cached duration.

### BufferedBytes

```csharp
long BufferedBytes { get; }
```

Current buffer size (bytes).

### IsReady

```csharp
bool IsReady { get; }
```

Whether the playable threshold has been reached.

### State

```csharp
BufferState State { get; }
```

Current buffer status.

### TargetDuration

```csharp
TimeSpan TargetDuration { get; set; }
```

Target buffer duration.

**Side Effect**: Besides updating the target duration, the setter also synchronously recalculates the "ready threshold" at a fixed rate (40% of the target)—
That is, by increasing the target size or setting it down, the threshold for determining whether the buffer is sufficient and playback can start will change accordingly, and the caller does not need to set it separately.

This write does not trigger events or immediately reassess the ready state: the new threshold takes effect on the next ready check;
Changes during runtime will only affect subsequent decisions and will not revert to the playback state.

### BufferProgressChanged

```csharp
event EventHandler<BufferProgressEventArgs>? BufferProgressChanged;
```

Buffer progress change events.

### StartAsync

```csharp
Task StartAsync(CancellationToken ct = default);
```

Start buffering (prefetching). Support cancellation.

**Parameters**

- `ct`: Cancel tokens; Prefetch stops after cancellation.

**Returns**: Prefetch tasks that end when completed or canceled.

### Stop

```csharp
void Stop();
```

Stop buffering.

### Clear

```csharp
void Clear();
```

Clear all buffers.
