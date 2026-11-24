---
title: "IMediaPlayer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaPlayer", "OpenAsync", "PlayAsync", "PauseAsync", "StopAsync", "SeekAsync", "State", "Position", "Duration", "Volume", "IsMuted", "PlaybackRate", "Mode", "Session", "StateChanged", "ErrorOccurred", "PositionChanged", "SubtitleReceived", "AudioDataAvailable", "VideoFrameAvailable", "VideoDroppedFrames"]
lang: "en-US"
---
# IMediaPlayer

Source: [`IMediaPlayer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs)

Top-level media player interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaPlayer : IDisposable, IAsyncDisposable
```

## Remarks

Thread safety: Public methods are thread-safe and can be called by any thread.

CancellationToken propagation strategy:

- Methods requiring CT: OpenAsync / StopAsync / SeekAsync
- Methods that do not require CT: PlayAsync / PauseAsync—not using CT is intentional: these two methods do not provide cancellation in their signatures,
Implementations may still include waiting (seek reset, pipeline start/stop), and failures are reported via `ErrorOccurred` instead of revoking tokens

## Members

### OpenAsync

```csharp
Task OpenAsync(IMediaSource source, CancellationToken ct = default);
```

Open the media source. Support cancellation.

**Parameters**

- `source`: Media source.
- `ct`: Cancel token.

**Returns**: Represents the task that opens the operation; Throws exceptions on failure (fatal error channel, see `ErrorOccurred` remarks).

### PlayAsync

```csharp
Task PlayAsync();
```

Start playback (pure memory state transition, no CT).

**Prerequisites**: The media source must have been successfully opened and be in Idle/Paused/Stopped/Ended status;
Not open, Opening/Buffering/Error state calls throw `InvalidOperationException` (no pipeline started, no state changed).

**Idempotent** :P laying state repeatedly calls and returns directly without restarting the pipeline.

**Failure Semantics**: Guards refuse to throw exceptions (no error events); Failures during the startup process
`ErrorOccurred` Report, do not throw (consistent with existing behavior).

**Returns**: Represents the task that plays the start operation; Failures during startup are reported by `ErrorOccurred`.

### PauseAsync

```csharp
Task PauseAsync();
```

Pause playback (pure memory state transition, no CT).

**Returns**: Represents a task that pauses state transitions.

### StopAsync

```csharp
Task StopAsync(CancellationToken ct = default);
```

Stop playing. No blocking, no waiting for pipeline threads to exit; `ct` Currently not accepted (reserved bit).

**Current Implementation**: The current implementation only issues stop requests and returns completed Tasks, **completely ignoring `ct`**,
Nor does it wait for the decoding/rendering thread to actually exit—stopping is done asynchronously. This parameter is retained to prevent signing from being broken when supporting "wait-and-hold stop" in the future.

Therefore, the caller must not return this method as a credential that is considered "the pipeline thread has stopped"; If this guarantee is needed, you should wait or redo it yourself
`OpenAsync`。

**Parameters**

- `ct`: Cancel tokens (reserved bits, currently implemented not accepted, see remarks).

**Returns**: Tasks completed immediately after issuing a stop request; **No** means the pipeline thread has exited.

### SeekAsync

```csharp
Task SeekAsync(TimeSpan position, CancellationToken ct = default);
```

Locate to the specified location. Support cancellation.

**Failure Semantics**: Location failure requires **throwing an exception** instead of returning silently—the current implementation triggers first
`ErrorOccurred`(`SeekFailed`) and throws the original exception, allowing the caller to handle only the exception,
You can also subscribe to events only.

**Prerequisite**: The media source must have been successfully opened and the status is not Opening/Error—there is no locative decapsulator available at this time,
Positioning cannot be performed. Violation of prerequisites throws `InvalidOperationException` (Guard refuses, no error event issued,
Still clock and buffer).

**Accuracy**: The upper layer only promises "positioning request issued," and the placement accuracy is determined by the unpacker
(See the return value semantics of `IMediaDemuxer.SeekAsync`).

**Parameters**

- `position`: Target location.
- `ct`: Cancel token.

**Returns**: Tasks representing positioning operations; Location failed to throw an exception (see remarks).

### State

```csharp
MediaState State { get; }
```

Current playback status.

### Position

```csharp
TimeSpan Position { get; }
```

Current playback position (read from Clock).

### Duration

```csharp
TimeSpan Duration { get; }
```

Total media time.

### Volume

```csharp
float Volume { get; set; }
```

Volume (0.0~1.0).

### IsMuted

```csharp
bool IsMuted { get; set; }
```

Whether to mute.

### PlaybackRate

```csharp
float PlaybackRate { get; set; }
```

Playback rate (1.0 = normal).

### Mode

```csharp
ProcessingMode Mode { get; set; }
```

Headless / server-side processing mode (default `RealTime`, switchable `Fastest`).
When setting `Fastest`, audio-video synchronization is disabled, and headless audio output is not throttled in real time,
All video frames are released directly, suitable for batch processing scenarios where transcoding or offline ML are faster and better.
Can be set before or after `OpenAsync`; Set to take effect immediately after opening (synchronized memory operation, no I/O).

### Session

```csharp
IMediaSession? Session { get; }
```

Current playback session (null when not open).

### StateChanged

```csharp
event EventHandler<MediaStateChangedEventArgs> StateChanged;
```

State change event.

Only state migration is notified, without carrying error details; Illegal/ignored migrations will not trigger this event.

### ErrorOccurred

```csharp
event EventHandler<MediaErrorEventArgs> ErrorOccurred;
```

Error event.

Only report errors, **no implicit state changes**: Subscribers must not assume that `State` has been changed to after receiving it
`Error`。

Fatal errors have another channel—`OpenAsync` throws an exception and the implementation sets the state to `Error`,
At this time, both this event and `StateChanged` will appear, but **the order of the two is not guaranteed**,
Subscribers must handle their own idempotent matters and must not infer causality based on order.

### PositionChanged

```csharp
event EventHandler<TimeSpan> PositionChanged;
```

Location change event.

### SubtitleReceived

```csharp
event EventHandler<SubtitleFrame?> SubtitleReceived;
```

Subtitle frame arrival event (null = no active subtitles, UI clears display).

### AudioDataAvailable

```csharp
event Action<AudioFrame>? AudioDataAvailable;
```

Audio sampled data arrival event (for consumption by visualizers such as AudioVisualizer).
**synchronously triggered** by the audio pipeline before each frame is submitted to output (audio pipeline thread),
Subscriber **read-only** incoming `AudioFrame`: must synchronize the required copy within its callback
(PCM bytes / sample format / sample rate / number of channels), **Do not dispose, do not hold references to this frame across threads**.
Does not trigger when not subscribed (zero additional overhead).

The event parameter is contract layer type `AudioFrame`, compliant with zero external references.

### VideoFrameAvailable

```csharp
event Action<VideoFrame>? VideoFrameAvailable;
```

Video frame arrival events (for consumption such as Skia soft rendering Presenter).
**Synchronously triggered** when the video pipeline branches at `SyncAction.Present` and the UI is subscribed (Skia mode)
(Video pipeline thread), subscriber **can only borrow read-only** incoming `VideoFrame`: must synchronously copy the required data within its callback
(Pixel buffer / width / pixel format), **Do not Dispose, do not hold the frame reference across threads**.
When unsubscribed (D3D11 native GPU mode), the pipeline is directly presented to the attached shared SwapChain, preventing this event from being triggered and maintaining zero additional overhead.

The event parameter is contract layer type `VideoFrame`, compliant with zero external references.

### VideoDroppedFrames

```csharp
long VideoDroppedFrames { get; }
```

Cumulative video frame loss (diagnostic/observable, read-only). Including the final frame of playback that was judged as Drop by the synchronizer rather than rendered.
