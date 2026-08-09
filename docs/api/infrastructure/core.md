# Core (`LingFan.Media.Core`)

The orchestration module. It owns the **session object graph** — the clock, demuxer, decoders, buffers, synchronizer, and the two pipelines — and is the only module that knows how they fit together.

> **DI discipline.** `MediaPlayerFactory` and `MediaClock` are **not** registered as shared Singletons. The factory is a stateless Singleton *constructor* that `new`s a fresh `MediaPlayer` (and its whole session) on every `Create()`; the clock is per-session so concurrent players never share a timebase. See [Extensions](/api/infrastructure/extensions) for the actual registration.

## MediaPlayer

Namespace: `LingFan.Media.Core`

The concrete `IMediaPlayer`. **Thread-safe**: public methods may be called from any thread. This is the type you resolve from DI (as `IMediaPlayer`) and the type returned by every factory.

```csharp
public sealed class MediaPlayer : IMediaPlayer
```

### Constructor

You normally never call this directly — `MediaPlayerFactory.Create()` builds it. Listed for completeness / advanced manual composition.

```csharp
public MediaPlayer(
    IMediaStreamFactory streamFactory,
    IMediaDemuxerFactory demuxerFactory,
    IVideoDecoderFactory videoDecoderFactory,
    IAudioDecoderFactory audioDecoderFactory,
    ISubtitleDecoderFactory? subtitleDecoderFactory,
    IVideoRendererFactory videoRendererFactory,
    IAudioOutputFactory audioOutputFactory,
    ILoggerFactory loggerFactory,
    ILogger<MediaPlayer> logger,
    IReadOnlyList<Func<VideoFrame, VideoFrame?>>? videoTransforms = null,
    IReadOnlyList<Func<AudioFrame, AudioFrame>>? audioTransforms = null,
    Action? videoTransformsReset = null,
    Action? audioTransformsReset = null,
    MediaPlayerOptions? options = null)
```

### Properties

| Property | Type | Notes |
|----------|------|-------|
| `State` | `MediaState` | Driven by an internal `PlaybackController` state machine; illegal transitions are ignored and logged. |
| `Position` | `TimeSpan` | Read from `MediaClock`. `0` before `OpenAsync` / after `StopAsync`. |
| `Duration` | `TimeSpan` | From the demuxer metadata. |
| `Volume` | `float` | Clamped to `0.0`–`1.0`. Applied to the audio output immediately (muted ⇒ `0`). |
| `IsMuted` | `bool` | When `true`, output volume is forced to `0` without changing `Volume`. |
| `PlaybackRate` | `float` | Forwards to `MediaClock.Speed` (affects timebase scaling). |
| `Mode` | `ProcessingMode` | `RealTime` (default, A/V sync + real-time pacing) vs `Fastest` (sync disabled — for transcode / offline ML). Applies immediately; in `Fastest` the synchronizer passes all frames and the headless audio output stops real-time throttling. |
| `Session` | `IMediaSession?` | The session created inside `OpenAsync`; `null` before open. |
| `VideoDroppedFrames` | `long` | Cumulative dropped video frames (diagnostics). |

### Events

| Event | Signature | Notes |
|-------|-----------|-------|
| `StateChanged` | `EventHandler<MediaStateChangedEventArgs>?` | State transition. |
| `ErrorOccurred` | `EventHandler<MediaErrorEventArgs>?` | Fatal/non-fatal errors. |
| `PositionChanged` | `EventHandler<TimeSpan>?` | Raised by a 33 ms timer reading the clock. |
| `SubtitleReceived` | `EventHandler<SubtitleFrame?>?` | `null` clears the display. |
| `AudioDataAvailable` | `Action<AudioFrame>?` | **Read-only borrow** — copy synchronously inside the callback; never `Dispose`/hold. |
| `VideoFrameAvailable` | `Action<VideoFrame>?` | **Read-only borrow** — same rules. Subscribing wraps your delegate in a `DelegateFrameSink` and subscribes to the internal `FrameChannel`. |

### Methods

| Method | Returns | Notes |
|--------|---------|-------|
| `OpenAsync(IMediaSource, CancellationToken ct = default)` | `Task` | 14-step open: stream → demuxer → `MediaSession` → clock/synchronizer/buffer manager → decoders → frame pools → renderer/audio output → track indices → video/audio pipelines → subtitle processor → pipeline host → network buffering → `Buffering` → `Idle` → `ApplyMode` → position timer. Real async (network connect + buffer start + stream read). Cancellable. |
| `PlayAsync()` | `Task` | Begins playback. **Replay path**: when `State == Ended`, it seeks to `0`, resets the playback clock, restarts the pipelines (`MediaPipelineHost.StartAsync` — video preroll → audio start → present release), then starts the clock. Returns a real `Task` (the start is awaited). |
| `PauseAsync()` | `Task` | Pure in-memory (`Clock.Pause` + host `Pause`). Returns `Task.CompletedTask`. |
| `StopAsync(CancellationToken ct = default)` | `Task` | Pure in-memory. Returns `Task.CompletedTask`. |
| `SeekAsync(TimeSpan position, CancellationToken ct = default)` | `Task` | Stops the buffer reader, clears queues, seeks the demuxer, flushes pipelines, then restarts buffering and **re-points the pipelines at the rebuilt packet queues** (required so a post-EOF replay does not read a completed channel). Real async. |
| `Dispose()` | `void` | Synchronous fallback cleanup — its **own** synchronous path (never `DisposeAsync().GetAwaiter().GetResult()`; that would be pseudo-async). |
| `DisposeAsync()` | `ValueTask` | Ordered, per-step `try/catch`-guarded release. Shared singleton renderers are deliberately **not** disposed here (their lifetime belongs to the renderer factory). |

> **Frame routing (the one rule).** Every video frame exits through a single `FrameChannel.Emit(frame)`. The public `VideoFrameAvailable` event is a façade over that channel. UI present sinks, headless compute sinks, and GPU zero-copy presenters all drink from the same channel and differ only in terminal action. There is no "headed vs. headless" fork.

## MediaPlayerFactory

Namespace: `LingFan.Media.Core`

Stateless Singleton constructor. Resolves `IEnumerable<...Factory>` collections (each backend registers via `TryAddEnumerable`) and builds a `MediaPlayer` from them.

```csharp
public sealed class MediaPlayerFactory : IMediaPlayerFactory
```

| Member | Notes |
|--------|-------|
| `Create()` | Takes the **first** element of each factory collection as the default backend group. Throws `InvalidOperationException` if no backend is registered. |
| `Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)` | Builds a player with an explicitly forced backend group. |
| ctor | `(IMediaStreamFactory, IEnumerable<IMediaDemuxerFactory>, IEnumerable<IVideoDecoderFactory>, IEnumerable<IAudioDecoderFactory>, IEnumerable<ISubtitleDecoderFactory>?, IVideoRendererFactory, IAudioOutputFactory, ILoggerFactory, IOptions<MediaPlayerOptions>?, videoTransforms?, audioTransforms?, videoTransformsReset?, audioTransformsReset?)` |

The constructor is invoked lazily by the keyed `"composer"` `IMediaPlayerFactory` registered in `AddLingFanMedia`, so all chained `AddXxx()` registrations are complete before it reads the builder's transform chains.

> In the open-box flow you do **not** use this factory directly. `BackendFallbackMediaPlayerFactory` calls the keyed `"composer"` instance after it selects a backend group.

## MediaClock

Namespace: `LingFan.Media.Core`

`sealed class : IMediaClock`. Pure in-memory, `Stopwatch`-based, `lock`-guarded for concurrent video/audio pipeline access. **Do not register as Singleton** — it is per-session.

| Member | Type | Notes |
|--------|------|-------|
| `Position` | `TimeSpan` | `base + elapsed * speed` while running, else `base`. |
| `Speed` | `float` | Changing speed re-bases `Position` and restarts the stopwatch. |
| `IsRunning` | `bool` | |
| `SyncSource` | `ClockSyncSource` | Default `Audio`. |
| `SyncThreshold` | `TimeSpan` | Default `50 ms`. |
| `DropThreshold` | `TimeSpan` | Default `200 ms`. |
| `Start()` / `Pause()` / `Reset()` | `void` | |
| `SeekTo(TimeSpan)` | `void` | Re-base to a position; restarts the stopwatch if running. |
| `SyncTo(TimeSpan masterPosition)` | `void` | Master-clock correction. Hard snap by default; with `LINGFAN_CLOCK_SMOOTH=1` applies a first-order low-pass + slew clamp (only large drifts hard-snap). Honors `LINGFAN_PACING_DIAG=1` for diagnostics. |

## MediaSession

Namespace: `LingFan.Media.Core`

`sealed class : IMediaSession`. Created inside `OpenAsync`; holds tracks, metadata, and track selection.

| Member | Type | Notes |
|--------|------|-------|
| `Source` | `IMediaSource` | The opened source. |
| `Metadata` | `MediaMetadata` | Container metadata. |
| `VideoTracks` / `AudioTracks` / `SubtitleTracks` | `IReadOnlyList<MediaTrack>` | Filtered by type. |
| `SelectedVideoTrack` / `SelectedAudioTrack` / `SelectedSubtitleTrack` | `MediaTrack?` | `lock`-guarded setters; default = `IsDefault` track, else first. |
| `Duration` | `TimeSpan` | |
| `IsLive` | `bool` | `true` for network sources. |
| `CloseAsync(CancellationToken ct = default)` | `Task` | Releases session-level info only; pipeline resources are released by `MediaPlayer.DisposeAsync`. |

## MediaPlayerOptions

Namespace: `LingFan.Media.Abstractions.Models.Settings` (shared config model, owned by Core's consumer)

| Property | Type | Default |
|----------|------|---------|
| `DefaultVolume` | `float` | `1.0f` |
| `DefaultMuted` | `bool` | `false` |
| `DefaultPlaybackRate` | `float` | `1.0f` |
| `EnableHardwareAcceleration` | `bool` | `true` |
| `VideoFrameQueueCapacity` | `int` | `30` |
| `AudioSampleQueueCapacity` | `int` | `60` |
| `LocalBufferTarget` | `TimeSpan` | `5 s` |
| `NetworkBufferTarget` | `TimeSpan` | `30 s` |
| `AudioOutputSampleRate` | `int?` | `null` (= source rate) |
| `AudioOutputChannels` | `int?` | `null` |
| `AudioOutputSampleFormat` | `SampleFormat?` | `null` |

Bound to `IOptions<MediaPlayerOptions>` by `AddLingFanMedia` so host configuration (e.g. `DefaultVolume`) propagates into Core's factory.

## MediaPipelineHost

Namespace: `LingFan.Media.Core`

Thin lifecycle wrapper around the video/audio/subtitle components. Methods are mostly synchronous `void` except the start orchestration.

| Member | Notes |
|--------|-------|
| `Attach(VideoPipeline?, AudioPipeline?, SubtitleProcessor?)` | Wire the three components. |
| `StartAsync()` | Orchestration order: start video → `WaitForPrerollAsync` → `SignalAudioReady` → `WaitForFirstFramePresentedAsync` → start audio → start subtitles. |
| `Pause()` / `Stop()` / `Flush()` / `FlushAsync()` / `Detach()` | |
| `PlaybackCompleted` | event fired when both A/V pipelines drain (drives `MediaPlayer` → `Ended`). |
| `VideoDroppedFrames` | `long` (forwarded). |

## VideoPipeline / AudioPipeline

Namespaces: `LingFan.Media.Core.Playback`

`sealed class : IAsyncDisposable, IDisposable`. Each runs a **decode loop** (`Task.Run`) decoupled from a **real-time pipeline loop** (long-running, highest priority) that peeks the packet queue, gates the first frame, and presents/submit under the master clock (with a watchdog that emits a frame if the master clock stalls).

### VideoPipeline

| Member | Notes |
|--------|-------|
| ctor | `(Channel<MediaPacket> packetQueue, IVideoDecoder, IVideoRenderer, FrameQueue, Synchronizer, IMediaClock, ILogger, IFramePool<VideoFrame>? framePool=null, IReadOnlyList<Func<VideoFrame,VideoFrame?>>? processors=null, Action? processorReset=null, Action<VideoFrame>? videoFrameSink=null)` |
| `IsRunning`, `FrameQueueSize`, `DroppedFrames` | diagnostics |
| `Start()` | |
| `WaitForPrerollAsync(ct)` | First-frame gate. |
| `SignalAudioReady()` | Releases the audio-start gate. |
| `WaitForFirstFramePresentedAsync(timeout, ct)` | A/V start ordering. |
| `Pause()` / `Stop()` / `Flush()` / `FlushAsync()` / `Dispose()` / `DisposeAsync()` | |
| `SetPacketQueue(Channel<MediaPacket>)` | internal — re-pointed after seek/EOF. |
| `Completed` | event. |

### AudioPipeline

| Member | Notes |
|--------|-------|
| ctor | `(Channel<MediaPacket>, IAudioDecoder, IAudioOutput, SampleQueue, Synchronizer, IMediaClock, ILogger, IFramePool<AudioFrame>? framePool=null, IReadOnlyList<Func<AudioFrame,AudioFrame>>? transforms=null, Action? effectReset=null, Action<AudioFrame>? audioDataSink=null)` |
| `IsRunning`, `SampleQueueSize`, `OutputLatency` | diagnostics |
| `StartAsync()` | preroll via `output.BeginStreamingAsync`. |
| `Pause()` / `Stop()` / `Flush()` / `FlushAsync()` / `Dispose()` / `DisposeAsync()` | |
| `SetPacketQueue(Channel<MediaPacket>)` | internal — re-pointed after seek/EOF. |
| _(internal)_ `SubmitBatch` | Audio frames are flushed to the output in small quanta (`MaxSubmitChunkMs = 40`) via an **internal** method — **not** part of the public API surface. Documented only to explain the throttling behavior. |
| `Completed` | event. |

## BufferManager

Namespace: `LingFan.Media.Core`

`sealed class : IBufferManager`. Owns the per-track packet queues (bounded `Channel`, capacity 256/512) and the buffering state machine.

| Member | Notes |
|--------|-------|
| ctor | `(IMediaDemuxer demuxer, ILogger<BufferManager> logger)` |
| `BufferedDuration` / `BufferedBytes` / `IsReady` / `State` (`BufferState`) / `TargetDuration` | diagnostics |
| `VideoPacketQueue` / `AudioPacketQueue` / `SubtitlePacketQueue` | `Channel<MediaPacket>` handed to the pipelines |
| `BufferProgressChanged` | `EventHandler<BufferProgressEventArgs>?` event |
| `SetTrackIndices(int videoTrackIndex, int audioTrackIndex)` | |
| `ConfigureForNetworkStream()` | |
| `StartAsync(ct)` / `Stop()` / `Clear()` / `Complete()` / `ResetQueues()` | `ResetQueues` rebuilds the channels after EOF so a replay does not read a completed channel |
| `ReaderTask` | internal background read task |

## Synchronizer (public concrete)

Namespace: `LingFan.Media.Core` — `public sealed class` (concrete; implements **no** contract interface). Drives A/V alignment.

- ctor `(IMediaClock clock, TimeSpan audioLatency = default)`
- `RealTimeSync` — when `false` (Fastest mode) it passes all video frames.
- `PresentationLatency` — default `1000/60 ms`.
- `SetMasterClockProvider(Func<TimeSpan>?)` — the audio playback-position provider (the default master clock; `LINGFAN_CLOCK_AUDIO_POS=1`).
- `OnAudioFrameSubmitted(AudioFrame)` — advances the master clock.
- `CheckVideoFrame(VideoFrame) → SyncAction` — returns `Present` / `Wait` / `Drop` (`public enum SyncAction` in `LingFan.Media.Core.Clock`, its own file — **not** nested in `Synchronizer`).
- `OnSeek(TimeSpan)` — resync on seek.

## `FramePool<T>` (public) & `FrameChannel` (internal)

| Type | Notes |
|------|-------|
| `FramePool<T> : IFramePool<T>, IDisposable where T : class` (`LingFan.Media.Core.Buffer`) | ctor `(Func<T> factory, Action<T>? reset = null, int maxSize = 16)`; `Rent()` / `Return(T)` (disposes on overflow) / `Dispose()`. AOT-friendly `ConcurrentStack`. |
| `FrameChannel : IFrameChannel` (`LingFan.Media.Core.Playback`, `internal`) | Thread-safe multicast. `Subscribe(IFrameSink)` → `IDisposable`, `Emit(VideoFrame)`, `Unsubscribe`. Sinks are **read-only borrowers** — never `Dispose`. The pipeline releases the frame in a `finally` after `Emit`. |
