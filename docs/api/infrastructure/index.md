# Infrastructure Layer

The `LingFan.Media` infrastructure is the **implementation stratum** that lives *below* the contract layer and *above* the platform backends (FFmpeg / MediaFoundation / VLC), renderers (D3D11 / Vulkan), and audio engines (WASAPI). It is where the playback graph is actually wired together.

Everything here depends on `LingFan.Media.Abstractions`, but the infrastructure modules themselves are split so that no single module depends on a concrete backend. The dependency direction is always **backend → abstractions**, never the reverse.

## Module map

| Module (project) | Role | Key types |
|------------------|------|-----------|
| `LingFan.Media.Core` | Playback orchestration — the session object graph | `MediaPlayer`, `MediaPlayerFactory`, `MediaClock`, `MediaSession`, `MediaPipelineHost`, `VideoPipeline`, `AudioPipeline`, `BufferManager`, `Synchronizer`, `FramePool`, `FrameChannel` |
| `LingFan.Media.Playback` | Backend fallback middleware (open-box, exception-driven) | `BackendFallbackMediaPlayerFactory`, `FallbackMediaPlayer` |
| `LingFan.Media.Extensions` | DI composition root — `AddLingFanMedia(...)` and `MediaBuilder` | `AddLingFanMedia`, `MediaBuilder`, `MediaOptions`, `CodecRegistry` |
| `LingFan.Media.Sources` | Source & stream abstraction + SSRF guard | `MediaStreamFactory`, `FileMediaSource`, `NetworkMediaSource`, `StreamMediaSource`, `SsrfGuard`, `SsrfConnectGuard` |
| `LingFan.Media.Formats` | Container detection & demuxer routing | `DemuxerFactory`, `FormatDetector`, `FormatSignature`, `MetadataExtractor` |
| `LingFan.Media.Consumers` | Headless / server-side consumers | `ConsumersExtensions`, `NoOpAudioOutput`, `NoOpVideoRenderer`, `ProcessingFrameSink`, `ProcessingAudioSink` |

## The two composition roots

- **`AddLingFanMedia(...)`** (Extensions) — the only supported way to register the infrastructure. It wires the Singleton factories and the Transient `IMediaPlayer`, mounts the SSRF-guarded HTTP clients, and returns a `MediaBuilder` for chaining backend/renderer/output registrations.
- **`BackendFallbackMediaPlayerFactory`** (Playback) — the default `IMediaPlayerFactory`. It resolves *which* backend opens a given source at runtime, remembers the result, and exposes `IBackendRegistry` for inspection.

## Reading order

If you are integrating the library, read in this sequence:

1. [Extensions](/api/infrastructure/extensions) — how to register.
2. [Core](/api/infrastructure/core) — the `MediaPlayer` surface you will call.
3. [Sources](/api/infrastructure/sources) — how to describe what to play (and the SSRF rules).
4. [Playback](/api/infrastructure/playback) — the fallback middleware (read-only; you normally never construct it directly).
5. [Formats](/api/infrastructure/formats) — container detection (read-only; used by the middleware).
6. [Consumers](/api/infrastructure/consumers) — headless / server processing.

> All types documented here are `public`. A few helper types (`FrameChannel`, `FormatSignature`, `CodecRegistry`, `FormatKey`) are `internal`/`concrete`; they are described only where they explain observable behavior, and you should never reference them directly. `Synchronizer` and `SyncAction` are `public` concrete types in `LingFan.Media.Core` (no contract interface) — documented because they explain the dropped-frame counter and A/V alignment, but you normally do not construct them directly.
