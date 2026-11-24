---
title: "IFrameChannel"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IFrameChannel.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFrameChannel", "Subscribe", "Unsubscribe", "Emit"]
lang: "en-US"
---
# IFrameChannel

Source: [`IFrameChannel.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IFrameChannel.cs)

Unified video frame delivery channel: decode output → channels → fan-out to all subscription sinks.
Both headed and headless share this passage; zero-copy is a difference in sink capability (GPU texture frames vs. CPU frames), not independent branches.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFrameChannel
```

## Remarks

Public event `IMediaPlayer.VideoFrameAvailable` i.e., the channel `Action{VideoFrame}` adapts to the appearance;
Advanced consumers (recordings, thumbnails, etc.) can directly implement `IFrameSink` subscriptions through this channel without delegation.

**Frame Ownership**: `Emit` Called by the pipeline within `try`,
Later, in `finally`, pipeline `ReturnFrame` releases the frame—**The channel and all sinks are read-only borrowed, never Dispose**.
Any Dispose within `OnFrame` will disrupt subsequent subscribers under multiple plays, which is a strict violation.

## Members

### Subscribe

```csharp
IDisposable Subscribe(IFrameSink sink);
```

Subscribe to a Sink and return the handle to unsubscribe (Dispose means unsubscribe).

**Parameters**

- `sink`: Frame consumer to subscribe to.

**Returns**: Unsubscribe from the handle, and Dispose means unsubscribe.

### Unsubscribe

```csharp
void Unsubscribe(IFrameSink sink);
```

Unsubscribe from a Sink (matched by reference/equivalence).

**Parameters**

- `sink`: The frame consumer to unsubscribe.

### Emit

```csharp
void Emit(VideoFrame frame);
```

Deliver one frame to all subscribers.

**Parameters**

- `frame`: Video frames to be delivered (read-only borrowed, see remarks).
