# Player & Session

## IMediaPlayer

Namespace: `LingFan.Media.Abstractions`

The top-level playback facade. **Thread-safe**: public methods may be called from any thread.

```csharp
public interface IMediaPlayer : IDisposable, IAsyncDisposable
```

### Methods

| Method | Returns | Notes |
|--------|---------|-------|
| `OpenAsync(IMediaSource source, CancellationToken ct = default)` | `Task` | Open a media source. Cancellable. Real async (demuxer open + buffer start + stream read). |
| `PlayAsync()` | `Task` | Begin playback. Pure in-memory state transition — no CT. Returns `Task.CompletedTask`. |
| `PauseAsync()` | `Task` | Pause. Pure in-memory. Returns `Task.CompletedTask`. |
| `StopAsync(CancellationToken ct = default)` | `Task` | Stop. Interface contract; impl may return `Task.CompletedTask`. |
| `SeekAsync(TimeSpan position, CancellationToken ct = default)` | `Task` | Seek. Real async (demuxer seek depends on stream seek/read). |

### Properties

| Property | Type | Notes |
|----------|------|-------|
| `State` | `MediaState` | Current playback state. |
| `Position` | `TimeSpan` | Current position (read from the clock). |
| `Duration` | `TimeSpan` | Total media duration. |
| `Volume` | `float` | Volume, 0.0–1.0. |
| `IsMuted` | `bool` | Mute flag. |
| `PlaybackRate` | `float` | Playback rate (1.0 = normal). |
| `Mode` | `ProcessingMode` | Headless / server processing mode. `Fastest` disables A/V sync and real-time pacing — for transcode / offline ML batch jobs. Settable before or after `OpenAsync`; takes effect immediately (sync, no I/O). |
| `Session` | `IMediaSession?` | Current session (null before open). |
| `VideoDroppedFrames` | `long` | Cumulative dropped video frames (diagnostics). If ≈ file frame count − presented frames at end, tail frames were dropped by the synchronizer. |

### Events

| Event | Signature | Notes |
|-------|-----------|-------|
| `StateChanged` | `EventHandler<MediaStateChangedEventArgs>` | State transition. |
| `ErrorOccurred` | `EventHandler<MediaErrorEventArgs>` | Error. |
| `PositionChanged` | `EventHandler<TimeSpan>` | Position update. |
| `SubtitleReceived` | `EventHandler<SubtitleFrame?>` | Subtitle frame arrived (`null` = clear display). |
| `AudioDataAvailable` | `Action<AudioFrame>?` | Audio PCM arrived. **Read-only borrow** — copy what you need synchronously inside the callback; never `Dispose` or hold the frame reference across threads. |
| `VideoFrameAvailable` | `Action<VideoFrame>?` | Video frame arrived. **Read-only borrow** — same rules. Fired only when UI is subscribed (Skia mode); in native GPU mode the pipeline presents directly. |

> **Frame ownership:** `AudioDataAvailable` / `VideoFrameAvailable` deliver a read-only borrow. The producer owns the frame; you must copy needed data synchronously and never `Dispose` it.

## IMediaSession

Namespace: `LingFan.Media.Abstractions`

Per-playback session state, created inside `OpenAsync`.

| Member | Type | Notes |
|--------|------|-------|
| `Source` | `IMediaSource?` | The opened source. |
| `Metadata` | `MediaMetadata?` | Container metadata. |
| `Tracks` | `IReadOnlyList<MediaTrack>` | All tracks. |
| `VideoTracks` / `AudioTracks` / `SubtitleTracks` | `IReadOnlyList<…TrackInfo>` | Filtered by type. |
| `SelectedVideoTrack` / `SelectedAudioTrack` / `SelectedSubtitleTrack` | `MediaTrack?` | Currently selected track. |
| `Duration` | `TimeSpan` | Duration. |
| `IsLive` | `bool` | Live stream flag. |
| `CloseAsync()` | `Task` | Close the session. |

## IMediaPlayerFactory

Namespace: `LingFan.Media.Abstractions`

Creates `IMediaPlayer` instances.

```csharp
public interface IMediaPlayerFactory
{
    IMediaPlayer Create();
    IMediaPlayer Create(IMediaDemuxerFactory demuxerFactory,
                        IVideoDecoderFactory videoDecoderFactory,
                        IAudioDecoderFactory audioDecoderFactory,
                        ISubtitleDecoderFactory? subtitleDecoderFactory = null);
}
```

- `Create()` — create a player using the DI-registered backend set (subject to fallback).
- `Create(...)` — force a specific backend group by passing its factory interfaces.

## IBackendRegistry

Namespace: `LingFan.Media.Abstractions`

Read-only view of registered backends.

```csharp
public interface IBackendRegistry
{
    IReadOnlyList<BackendDescriptor> Backends { get; }
}
```

## BackendDescriptor

Namespace: `LingFan.Media.Abstractions`

A **read-only description of a registered backend** — it holds **factory interfaces** (DI-resolved Singleton services), **not** player/backend instances.

```csharp
public sealed record BackendDescriptor(
    string Name,
    IMediaDemuxerFactory Demuxer,
    IVideoDecoderFactory VideoDecoder,
    IAudioDecoderFactory AudioDecoder,
    ISubtitleDecoderFactory? SubtitleDecoder);
```

When a backend group is selected, pass these factory interfaces to `IMediaPlayerFactory.Create(...)` to build a session. **Do not confuse lookup (interfaces) with instance (player).**

## MediaBackendUnsupportedException

Namespace: `LingFan.Media.Abstractions`

`sealed class`. Thrown by the fallback middleware when **every** registered backend fails to open the source.
