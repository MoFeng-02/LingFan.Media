# Extensions / DI (`LingFan.Media.Extensions`)

The **composition root**. This is the only supported entry point for registering the infrastructure. Everything here is synchronous *configuration* (no I/O, no async) — it builds the DI container; the actual work happens later inside `OpenAsync`.

## AddLingFanMedia

Namespace: `LingFan.Media.Extensions`

```csharp
public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    Action<MediaOptions>? configure = null);

public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    MediaOptions options);
```

Registers the core infrastructure and returns a `MediaBuilder` for chaining backend/renderer/output registrations.

### What it registers

**Infrastructure (Singleton — stateless factories / shared resources):**

- `IMediaStreamFactory` → `MediaStreamFactory` (holds `IHttpClientFactory`).
- `IFormatDetector` → `FormatDetector` (contract-clean; the middleware depends only on the contract).
- `ICodecRegistry` → `CodecRegistry` (static table, pure memory).
- `AddHttpClient()` plus two named clients:
  - `"LingFanMedia"` — `SocketsHttpHandler` with `ConnectCallback = SsrfConnectGuard.ConnectAsync` (DNS pinning, closes the rebinding TOCTOU window).
  - `"LingFanMedia_Insecure"` — same guard, but with certificate validation disabled (only for sources that explicitly set `AllowInsecureHttps`).
- `IMediaPlayer` (Transient) — resolved through `IMediaPlayerFactory.Create()`.
- `IOptions<MediaOptions>` and `IOptions<MediaPlayerOptions>` (the latter wires host `DefaultVolume` into Core's factory).
- Keyed `"composer"` `IMediaPlayerFactory` — the core `MediaPlayerFactory` (lazy; reads the builder's transform chains after all `AddXxx()` calls complete).
- `BackendFallbackMediaPlayerFactory` (Singleton), registered **twice so both contracts point to the same instance**:
  - `IMediaPlayerFactory` → the fallback factory
  - `IBackendRegistry` → the same fallback factory

> **Why two registrations to one instance?** If the factory and registry resolved to *different* objects, each would hold its own fallback `Cache`, breaking the hit-memory semantic. They must be the same Singleton.

**Not registered here:** `IMediaDemuxerFactory` / decoder factories / `IVideoRendererFactory` / `IAudioOutputFactory`. Backends register those via `TryAddEnumerable` (so multiple backends co-exist and fall back in registration order). Call e.g. `.AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()` on the returned `MediaBuilder`.

## MediaBuilder

Namespace: `LingFan.Media.Extensions`

```csharp
public sealed class MediaBuilder
```

Fluent builder returned by `AddLingFanMedia`. Its constructor is `internal` — you can only obtain one via `AddLingFanMedia`.

### Properties

| Property | Type | Notes |
|----------|------|-------|
| `Services` | `IServiceCollection` | The DI collection that `AddXxx()` extensions register into. |
| `Options` | `MediaOptions` | The global configuration object. |

### Methods

| Method | Returns | Notes |
|--------|---------|-------|
| `WithAudioPipeline(AudioPipelineConfig config)` | `MediaBuilder` | Injects the audio effect/transform chain + reset hook (from `config.ToTransforms()` / `config.ResetEffects()`). |
| `WithAudioTransforms(IReadOnlyList<Func<AudioFrame, AudioFrame>> transforms, Action? reset = null)` | `MediaBuilder` | Injects an already-composed audio transform chain directly. |
| `WithVideoPipeline(VideoPipelineConfig config)` | `MediaBuilder` | Injects the video post-processing chain + reset hook. |
| `WithVideoTransforms(IReadOnlyList<Func<VideoFrame, VideoFrame?>> transforms, Action? reset = null)` | `MediaBuilder` | Injects an already-composed video transform chain directly. |

All four return `this` for chaining. If none are called, the transform chains stay `null` → **fully V1-compatible** (no post-processing).

> The transform fields are `internal` `Func<...>`/`Action` delegates — neutral BCL types. Core never references the Video/Audio modules; dependency inversion holds.

## MediaOptions

Namespace: `LingFan.Media.Extensions`

```csharp
public sealed class MediaOptions
```

Global configuration, read by `AddLingFanMedia` and bound to `IOptions<MediaOptions>`.

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `DefaultVideoRenderer` | `Type?` | `null` (auto) | |
| `DefaultAudioOutput` | `Type?` | `null` (auto) | |
| `PreferredBackend` | `string?` | `null` (auto) | e.g. `"FFmpeg"`. |
| `EnableHardwareDecode` | `bool` | `true` | |
| `EnableAutoBackendSelection` | `bool` | `false` | |
| `BufferTargetDuration` | `TimeSpan` | `5 s` | |
| `EnableLogging` | `bool` | `true` | |
| `LogLevel` | `LogLevel` | `Information` | Logging config is stored here; the host's logging host reads it (Extensions depends only on `Logging.Abstractions`). |
| `DefaultVolume` | `float` | `1.0f` | Propagated into `MediaPlayerOptions.DefaultVolume` via `IOptions`. |

`CopyTo(MediaOptions target)` is `internal` and copies all fields (used by the `MediaOptions` overload of `AddLingFanMedia`).

## CodecRegistry (internal)

Namespace: `LingFan.Media.Extensions` — `internal sealed class : ICodecRegistry`.

A **static, AOT-friendly** mapping table. Implements `ICodecRegistry`:

- `IsCodecSupported(ContainerFormat, VideoCodec)` / `IsCodecSupported(ContainerFormat, AudioCodec)`
- `GetDefaultVideoCodec(ContainerFormat)` / `GetDefaultAudioCodec(ContainerFormat)`

Covers MP4 / MKV / AVI / TS / WebM / FLV video+audio codec tables. Registered as Singleton. You consume it only through the `ICodecRegistry` contract; do not reference the concrete type.
