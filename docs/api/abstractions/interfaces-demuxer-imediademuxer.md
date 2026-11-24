---
title: "IMediaDemuxer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaDemuxer", "OpenAsync", "Tracks", "Metadata", "ReadPacketAsync", "SeekAsync", "Close"]
lang: "en-US"
---
# IMediaDemuxer

Source: [`IMediaDemuxer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxer.cs)

Unwrapper interface. Put the container (MP4/MKV/... ) is split into independent orbital packets.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaDemuxer : IMediaComponent
```

## Remarks

Interfaces are defined in Abstractions, and actual implementations (such as FFmpegDemuxer) are in the Backends module.

Inherits from IMediaComponent, with two release paths: Dispose() + DisposeAsync().

Thread safety: Tracks/Metadata after OpenAsync can be read across threads; ReadPacketAsync/SeekAsync cannot be called concurrently.

## Members

### OpenAsync

```csharp
Task OpenAsync(IMediaStream stream, CancellationToken ct = default);
```

Open the media stream to perform format detection and track parsing.

**Parameters**

- `stream`: Media data stream.
- `ct`: Cancel token.

**Returns**: Tasks that end when format detection and orbital resolution are complete.

### Tracks

```csharp
IReadOnlyList<MediaTrack> Tracks { get; }
```

List of parsed tracks.

### Metadata

```csharp
MediaMetadata Metadata { get; }
```

Container metadata.

### ReadPacketAsync

```csharp
ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default);
```

Read the next data packet. Returning null indicates the end of the stream.

**Parameters**

- `ct`: Cancel token.

**Returns**: Next packet; `null` indicates the end of the stream.

### SeekAsync

```csharp
Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);
```

Locate to the specified location. `true` Only indicates "Location request accepted" and does not guarantee exact placement.

**Return value semantics**: `true` indicates the request is accepted and subsequent reads resume from a new location;
Containers usually only locate keyframes/block boundaries, so ** does not necessarily mean ** is exactly on `position`.
The party requiring precise landing must either read back the actual position confirmation or discard frames before the target time themselves.

**Failure Semantics**: Returns `false` when the container itself is unlocated, without throwing exceptions.

**State Semantics**: When released (after `Dispose` / `DisposeAsync`), calls must be thrown **
`ObjectDisposedException`。 As for "created but not yet `OpenAsync`" and
The two states "`Close` not released after `Close`" are **not uniformly specified** in this interface: implementation can be returned
`false` means not positioned, or `InvalidOperationException` indicates incorrect call timing,
However, the implementation documentation must specify which method is used; The caller must not rely on the specific actions of either side,
Ensure that `OpenAsync` succeeds first, and do not call again after release.

**Parameters**

- `position`: Target location.
- `ct`: Cancel token.

**Returns**: `true` indicates that the positioning request was accepted (non-exact landing point); `false` indicates the container cannot be positioned.

### Close

```csharp
void Close();
```

Close the unwrapper.
