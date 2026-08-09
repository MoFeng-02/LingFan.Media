# Playback Middleware (`LingFan.Media.Playback`)

The **backend fallback middleware** — the open-box, exception-driven dispatcher that decides *which* registered backend opens a given source. It is the default `IMediaPlayerFactory` you get from `AddLingFanMedia`, but you normally never construct it yourself.

Two principles:

1. **Contract-clean.** `BackendFallbackMediaPlayerFactory` depends only on `Abstractions` + `Microsoft.Extensions.DependencyInjection.Abstractions`. It never references a concrete backend, renderer, or UI type.
2. **Lookup ≠ instance.** The factory holds *factory interfaces* (Singleton, stateless). When a backend is selected, those interfaces are handed to the core `"composer"` factory to build the actual `IMediaPlayer` session. Never confuse the descriptor with the player instance.

## BackendFallbackMediaPlayerFactory

Namespace: `LingFan.Media.Playback`

```csharp
public sealed class BackendFallbackMediaPlayerFactory : IMediaPlayerFactory, IBackendRegistry
```

### Constructor

```csharp
public BackendFallbackMediaPlayerFactory(
    IServiceProvider sp,
    ILoggerFactory? loggerFactory = null,
    IMediaStreamFactory? streamFactory = null,
    IFormatDetector? formatDetector = null)
```

Resolves the keyed `"composer"` `IMediaPlayerFactory` (throws `InvalidOperationException` if `AddLingFanMedia` was not called).

### Members

| Member | Type | Notes |
|--------|------|-------|
| `Create()` | `IMediaPlayer` | Returns a **not-yet-opened** `FallbackMediaPlayer`; backend selection is deferred to its `OpenAsync`. |
| `Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)` | `IMediaPlayer` | Forces a specific backend group — delegates straight to the core composer. No fallback. |
| `Backends` | `IReadOnlyList<BackendDescriptor>` | **Lazily** aggregated by DI registration order. `demuxer`/`video`/`audio` factories are aligned by index; `subtitle` factories are matched **by backend name** (a `Dictionary<string, ISubtitleDecoderFactory>`) to prevent mis-pairing (e.g. FFmpeg's subtitle factory must not be matched to the MF group). |

### Memory caches (cross-instance, cross-source)

| Cache | Key | Purpose |
|-------|-----|---------|
| `Cache` | `ConcurrentDictionary<string, int>` (`source.Identifier` → backend index) | File-level memory: same source re-opens the previously working backend first. |
| `FormatCache` | `ConcurrentDictionary<FormatKey, int>` (`(ContainerFormat, VideoCodec)` → backend index) | Format-level memory: same container+video-codec reuses the winning backend, skipping known-bad ones. `mp4/H264` and `mp4/H265` are remembered independently. |

`FormatKey` is an `internal readonly record struct(ContainerFormat Container, VideoCodec Video)`.

### `NameOf(object factory)` (private)

Derives a friendly backend name by stripping the longest matching suffix: `SubtitleDecoderFactory` → `DecoderFactory` → `DemuxerFactory` → `Factory`. The subtitle-suffix is stripped **first** so a subtitle factory's name matches its sibling demuxer's name.

## FallbackMediaPlayer

Namespace: `LingFan.Media.Playback`

```csharp
public sealed class FallbackMediaPlayer : IMediaPlayer
```

A thin wrapper that forwards every `IMediaPlayer` member to the runtime-selected inner player (`_active`). It holds **no** backend logic of its own.

### Constructor

```csharp
public FallbackMediaPlayer(BackendFallbackMediaPlayerFactory owner, ILogger? logger)
```

### `OpenAsync(IMediaSource source, CancellationToken ct = default)`

The only interesting method. Sequence:

1. If a previous `_active` exists (re-open), detach its events and `DisposeAsync` it (NativeCallGate discipline — no native leak, no double events).
2. **Format memory probe** (local files only): `CreateAsync` a probe stream, `IFormatDetector.DetectProfile` it, to learn `(container, video)`. Skipped for network/non-seekable streams. Failures degrade to full fallback — never block playback.
3. Decide the **fallback start index**: `FormatCache` (precise) → `Cache` (file) → `0` (full scan). All three wrap around, so a stale memory entry still falls through to other backends.
4. Try each backend from the start index (round-robin). On success: write `FormatCache[(container, realVideo)]` and `Cache[source.Identifier]`, apply local pre-`Open` settings, attach events (sender = `this`), return.
5. On `OperationCanceledException`: dispose the partial inner and rethrow (respect cancellation).
6. On any other exception: log a warning, dispose the partial inner, try the next backend.
7. If all backends fail: throw `MediaBackendUnsupportedException(source.Identifier)`.

### Members

All `IMediaPlayer` members forward to `_active` (returning safe defaults — `Stopped`/`Zero`/`0` — when `_active` is `null`). Local settings (`_volume`, `_isMuted`, `_playbackRate`, `_mode`) are stored pre-open and pushed into the inner player on open (`ApplyLocalSettings`).

`PlayAsync` / `PauseAsync` / `StopAsync` / `SeekAsync` forward to `_active` (or return `Task.CompletedTask` when not yet opened). `Dispose` / `DisposeAsync` detach events and delegate to the inner player's NativeCallGate-guarded release.

## BackendDescriptor (cross-reference)

Defined in the contract layer — see [Player & Session (Abstractions)](/api/abstractions/player#backenddescriptor). It is the read-only, factory-interface-carrying description that `Backends` returns.

## SyncAction (public enum)

`public enum SyncAction : int` in `LingFan.Media.Core.Clock` (its own file, **not** nested in `Synchronizer`): `Present` / `Wait` / `Drop`. The verdict returned by `Synchronizer.CheckVideoFrame`. Documented here only to explain the dropped-frame counter exposed on `MediaPlayer.VideoDroppedFrames`.
