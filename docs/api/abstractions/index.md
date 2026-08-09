# Contract Layer (Abstractions)

The `LingFan.Media.Abstractions` project is the **contract layer** — the foundation of the system. It contains **zero implementation**: only interface signatures, auto-properties, pure data models, enums, and event-argument types. It depends on nothing outside the BCL and `Microsoft.Extensions.Logging.Abstractions`.

## What lives here

| Category | Count | Examples |
|----------|-------|----------|
| Interfaces | 43 | `IMediaPlayer`, `IFrameChannel`, `IMediaClock`, `IVideoRenderer`, `IAudioOutput`, `IMediaDemuxer`, `IFormatDetector` |
| Models | 23 | `VideoFrame`, `AudioFrame`, `SubtitleFrame`, `MediaPacket`, `MediaTrack`, `MediaFormatProfile` |
| Enums | 22 | `MediaState`, `VideoCodec`, `PixelFormat`, `ContainerFormat`, `ClockSyncSource`, `GPUApiType` |
| Events | 5 | `MediaStateChangedEventArgs`, `MediaErrorEventArgs`, `BufferProgressEventArgs`, `TrackChangedEventArgs`, `LogEventArgs` |

## The two principles

1. **Zero external references.** Every parameter and return type is either a BCL type (`IDisposable`, `Memory<byte>`, `Stream`, `CancellationToken`, `Task` / `ValueTask`) or a type already declared in `Abstractions`. No backend, renderer, or UI concrete type may appear in a contract signature.
2. **Zero implementation.** Only signatures, auto-properties, and pure data models (including `Dispose` releasing the type's own neutral resources). No business logic, no `new` of concrete types.

> Because the layer is zero-external-reference, backends / renderers / outputs / platforms / UI can be added, removed, or rewritten without touching the contract. That is the real payoff of dependency inversion.

## Sub-pages

- [Player & Session](/api/abstractions/player) — `IMediaPlayer`, `IMediaSession`, `IMediaPlayerFactory`, `BackendDescriptor`
- [Frames & Resources](/api/abstractions/frames) — `IFrameChannel`, `IFrameSink`, `VideoFrame`, `AudioFrame`, `MediaPacket`, `IFrameResource`
- [Clock & Buffering](/api/abstractions/clock) — `IMediaClock`, `IBufferManager`, `IMediaComponent`
- [Source & Demux](/api/abstractions/demuxer) — `IFormatDetector`, `IMediaDemuxer`, `IMediaSource`, `IMediaStream`
- [Decoders & Codecs](/api/abstractions/decoders) — `IVideoDecoder`, `IAudioDecoder`, `ISubtitleDecoder`, `ICodecRegistry`
- [Renderers & Outputs](/api/abstractions/renderers) — `IVideoRenderer`, `IAudioOutput`, `IAudioEngine`
- [Enumerations](/api/abstractions/enums) — all 22 enumerations
- [Events & Exceptions](/api/abstractions/events-exceptions) — event args and exception types
