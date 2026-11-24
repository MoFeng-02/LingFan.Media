---
title: "BackendDescriptor"
type: "record"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/BackendDescriptor.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["BackendDescriptor", "Name", "Demuxer", "VideoDecoder", "AudioDecoder", "SubtitleDecoder"]
lang: "en-US"
---
# BackendDescriptor

Source: [`BackendDescriptor.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/BackendDescriptor.cs)

A read-only description of a registered backend (factory interface collection, non-instance).
Aggregated by `IBackendRegistry` in DI registration order, for middleware/advanced consumers to view and select backends.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed record BackendDescriptor(
    string Name,
    IMediaDemuxerFactory Demuxer,
    IVideoDecoderFactory VideoDecoder,
    IAudioDecoderFactory AudioDecoder,
    ISubtitleDecoderFactory? SubtitleDecoder);
```

**Parameters**

- `Name`: Backend-friendly names (such as "FFmpeg" / "MediaFoundation" / "VLC").
- `Demuxer`: Decapsulator factory interface.
- `VideoDecoder`: Video decoder factory interface.
- `AudioDecoder`: Audio decoder factory interface.
- `SubtitleDecoder`: Subtitle decoder factory interface (optional; Only partial backend registration).

## Remarks

What it holds is the **factory interface** (DI-parsed Singleton stateless service), **not the ** player/backend instance.

After hitting a backend group, these factory interfaces should be passed on
`Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)`
Go create a Session-level object. ** lookup (interface) and instance (instance) should not be confused. **
