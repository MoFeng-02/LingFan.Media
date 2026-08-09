# Clock & Buffering

## IMediaClock

Namespace: `LingFan.Media.Abstractions`

The media clock — the core of A/V synchronisation. **Pure in-memory**, no I/O, no `CancellationToken`. **Thread-safe** (lock / Interlocked): both the video and audio pipelines read it. **Must not be registered as Singleton** — multiple players would contend for one clock.

```csharp
public interface IMediaClock
{
    TimeSpan Position { get; }
    float Speed { get; set; }
    bool IsRunning { get; }
    ClockSyncSource SyncSource { get; set; }
    TimeSpan SyncThreshold { get; set; }    // default 50 ms
    TimeSpan DropThreshold { get; set; }    // default 200 ms
    void Start();
    void Pause();
    void Reset();
    void SeekTo(TimeSpan position);
    void SyncTo(TimeSpan masterPosition);
}
```

| Member | Notes |
|--------|-------|
| `Position` | Current clock position. |
| `Speed` | Clock speed (1.0 = normal). |
| `IsRunning` | Whether the clock is ticking. |
| `SyncSource` | Master sync source: `Audio` / `Video` / `System`. |
| `SyncThreshold` | Max drift before the synchronizer nudges (default 50 ms). |
| `DropThreshold` | Drift beyond which a frame is dropped (default 200 ms). |
| `Start()` / `Pause()` / `Reset()` | Lifecycle (pure in-memory). |
| `SeekTo(pos)` | Jump to a position. |
| `SyncTo(masterPosition)` | Align to the master clock. |

> Audio is the usual master clock (`LINGFAN_CLOCK_AUDIO_POS = 1`). Video frames are presented/dropped by comparing their PTS against this clock.

## IBufferManager

Namespace: `LingFan.Media.Abstractions`

Manages the decode buffer.

| Member | Type | Notes |
|--------|------|-------|
| `BufferedDuration` / `BufferedBytes` | `TimeSpan` / `long` | Buffered amount. |
| `IsReady` | `bool` | Ready to play. |
| `State` | `BufferState` | `Empty` / `Buffering` / `Ready` / `Starved`. |
| `TargetDuration` | `TimeSpan` | Target buffer duration. |
| `BufferProgressChanged` | `event` | Progress notification. |
| `StartAsync(ct)` / `Stop()` / `Clear()` | methods | Buffer control. |

## IMediaComponent

Namespace: `LingFan.Media.Abstractions`

The common base interface for every pipeline component (decoders, demuxer, renderers, audio output).

```csharp
public interface IMediaComponent : IDisposable, IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
}
```

`IVideoDecoder` / `IAudioDecoder` / `ISubtitleDecoder` / `IVideoRenderer` / `IAudioOutput` / `IMediaDemuxer` all derive from this.
