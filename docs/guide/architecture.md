# Architecture

LingFan.Media is organised as a **contract layer** (`Abstractions`) above a set of **infrastructure modules**, with pluggable **backends**, **renderers**, **outputs**, and a **UI** layer on top. The defining principle: *the contract layer is the foundation; everything else adapts to it.*

## Dependency inversion is the whole point

`Abstractions` contains only signatures, auto-properties, and pure data models. It depends on **nothing** outside BCL + `Microsoft.Extensions.Logging.Abstractions`. Because of that:

- Backends, renderers, outputs, platforms, and UI can be added, removed, or rewritten without touching the contract.
- The contract layer is allowed to *grow* (new method signatures may be added), but it must **never reference a concrete backend type** — that would break the dependency-inversion boundary.

> **Rule of thumb:** if a type is referenced by two or more layers, it belongs in `Abstractions`. If only one module uses it, it stays in that module.

## The 12 logical modules

| # | Module | Responsibility |
|---|--------|----------------|
| 01 | `Abstractions` | Cross-layer contracts: interfaces, models, enums, events (zero implementation) |
| 02 | `Core` | `MediaPlayer`, `MediaSession`, `VideoPipeline`, `AudioPipeline`, `MediaClock`, `Synchronizer`, `BufferManager` |
| 03 | `Sources` | `FileMediaSource` / `NetworkMediaSource` / `StreamMediaSource` + `MediaStreamFactory` |
| 04 | `Formats` | `FormatDetector`, `DemuxerFactory`, metadata extraction |
| 05 | `Video` | Video track, processor chain, deinterlace/scale/color, stats |
| 06 | `Audio` | Audio track, mixer, volume, effects chain, stats |
| 07 | `Backends` | `FFmpeg` / `VLC` / `MediaFoundation` (real); `WebRTC` (stub) |
| 08 | `Renderers` | `D3D11` (real); `Vulkan` / `Metal` / `OpenGL` (stubs/partial) |
| 09 | `Outputs` | `WASAPI`, `OpenAL`, `OpenSL ES`, `AAudio`, … |
| 10 | `Platforms` | Platform capability detection & interop |
| 11 | `Avalonia` | `VideoView`, `MediaControl`, Skia / Composition presenters |
| 12 | `Extensions` | `AddLingFanMedia()`, `MediaBuilder`, codec registry, backend auto-selection |

## Frame routing — one path, many sinks

There is exactly **one** route a video frame takes out of the pipeline:

```
VideoPipeline → _videoFrameSink(frame) → _frameChannel.Emit(frame) → every subscribed IFrameSink
```

`MediaPlayer` injects a single sink delegate into the pipeline: `frame => _frameChannel.Emit(frame)`. The pipeline never branches on backend or renderer. `FrameChannel.Emit` fans out to all subscribed `IFrameSink`s. A headed renderer (`Composition` / `Skia` / `D3D11`) and a headless consumer (`ProcessingFrameSink`) implement the *same* `IFrameSink` contract and drink from the same channel — they differ only in terminal action (present vs. feed an algorithm) and capability (can they consume a GPU texture frame).

> **Zero-copy is a Sink capability, not a separate branch.** Whether a frame is presented zero-copy depends on what the Sink can do, not on which fork of code produced it.

## Unified output ports

Production and consumption are deliberately decoupled. "Making changes" only ever happens *inside* the unified ports.

- **Video output port = `IFrameChannel` + `IFrameSink`.** All three decode backends produce frames into this one channel. New terminal capabilities (recorder Sink, thumbnail Sink, transform chain) are added by subscribing a new `IFrameSink` — no backend or pipeline changes.
- **Audio output port = `IAudioOutput`.** `AudioPipeline` normalises every backend's decoded audio into `IAudioOutput.Submit`. Audio is submitted **directly to `IAudioOutput` and bypasses the synchronizer** — the audio/video asymmetry is intentional; we do not add a sync branch to audio just for symmetry. The headless consumer (`ProcessingAudioSink`) shares the `IAudioOutput` contract with `WASAPIOutput`.

## Headless-first

The headed path is literally *the headless pipeline plus a subscribing Present Sink*:

```
IVideoRenderer.Present  ←  VideoView.PresentFrame  ←  (subscribed to IFrameChannel)
```

A `VideoView` subscribes to the frame channel and bridges it to `IVideoRenderer.Present`. The contract stays neutral; only the terminal Sink differs.

## Session isolation & DI layering

- **System-level factories are `Singleton`.** `IMediaStreamFactory`, `IFormatDetector`, `ICodecRegistry`, the backend factories, and `BackendFallbackMediaPlayerFactory` live for the process lifetime.
- **Per-playback state is `Transient` (a Session).** `MediaPlayer` creates its `MediaSession`, `MediaClock`, `BufferManager`, and pipelines inside `OpenAsync`. Each `IMediaPlayer` owns an independent session; tearing one down never disturbs another.

> The public facade is `MediaPlayer` (in `Core`) and `FallbackMediaPlayer` / `BackendFallbackMediaPlayerFactory` (in `Playback`). There is no single `MediaGraph` type — playback is composed from these facades plus the DI container.
