---
title: "IMediaDemuxerFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxerFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaDemuxerFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# IMediaDemuxerFactory

Source: [`IMediaDemuxerFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxerFactory.cs)

Decapsulator factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaDemuxerFactory
```

## Remarks

Singleton factory, statusless. Each time Create() returns a new instance (each playback is new, no sharing).

Prioritize using `CreateAsync` (asynchronous format probes without blocking threads, supports CT).

## Members

### Create

```csharp
IMediaDemuxer Create(IMediaStream stream);
```

Create the corresponding IMediaDemuxer instance based on the media stream.

**Parameters**

- `stream`: Media data stream.

**Returns**: Newly created decapsulator instance (not open, must call `OpenAsync`).

### CreateAsync

```csharp
Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default);
```

Asynchronously create the corresponding IMediaDemuxer instance based on the media stream.

**Parameters**

- `stream`: Media data stream.
- `ct`: Cancel token.

**Returns**: Newly created unwrapper instance.
