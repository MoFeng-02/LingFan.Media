---
title: "MediaPacket"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/MediaPacket.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaPacket", "TrackIndex", "Data", "Timestamp", "Duration", "KeyFrame", "Width", "Height", "Stride", "SampleRate", "Channels", "Format", "_decodedFrameResource", "_disposed", "TakeDecodedFrameResource", "Dispose"]
lang: "en-US"
---
# MediaPacket

Source: [`MediaPacket.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/MediaPacket.cs)

Compressed data packets after decapsulation. Implement `IDisposable`.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaPacket : IDisposable
```

## Remarks

Memory Security:

- zero-copy path: FFmpegDemuxer shares FFmpeg's internal buffer through av_packet_clone reference count,
`Data` directly maps native memory, with the owner passing in as neutral `IDisposable`
- Copy path (no dataOwner): MediaPacket independently owns a managed data copy (compatible with non-referenced counting sources)
- `Dispose` Release the owner (native reference count minus one); No access allowed after release `Data`

## Members

### TrackIndex

```csharp
public int TrackIndex { get; }
```

Index of the assigned track.

### Data

```csharp
public ReadOnlyMemory<byte> Data { get; }
```

Compress data (read-only view, with the underlying buffer owned by the packet).

### Timestamp

```csharp
public TimeSpan Timestamp { get; }
```

Timestamp (PTS).

### Duration

```csharp
public TimeSpan Duration { get; }
```

Packet duration.

### KeyFrame

```csharp
public bool KeyFrame { get; }
```

Whether keyframes are available.

### Width

```csharp
public int Width { get; }
```

Decode frame width (only pass-through packets are meaningful, compressed packets are 0).

### Height

```csharp
public int Height { get; }
```

Decode frame height (compress packet to 0).

### Stride

```csharp
public int Stride { get; }
```

Decode the frame line span bytes (compressed packet to 0).

### SampleRate

```csharp
public int SampleRate { get; }
```

Audio sampling rate (compressed packet is 0).

### Channels

```csharp
public int Channels { get; }
```

Number of audio channels (compressed packet to 0).

### Format

```csharp
public SampleFormat Format { get; }
```

Audio sampling format (compression packet is the default).

### _decodedFrameResource

```csharp
public IFrameResource? DecodedFrameResource => _decodedFrameResource;
```

Decoded frame resource (optional). Only the "integrated unpacking + decoding" pass-through backend (such as MF SourceReader with built-in hardware decoding or VLC) will carry it;
The pure dewrapping backend (FFmpeg compressed packet) is always `null`.

**Zero-copy semantics**: This resource is usually GPU textures (`IGpuTextureResource`),
Extracted directly from the native DXGI/hardware surface during the reading phase by demuxer, with no system memory back-and-forth.

**Ownership**: Owned by default by this packet, released cascading at `Dispose`.
To transfer resources to `VideoFrame`, the decoder must call `TakeDecodedFrameResource`
Explicitly transfer ownership to avoid prematurely destroying textures still in the render pipeline when packets are released.

### null

```csharp
public bool HasDecodedFrameResource => _decodedFrameResource != null;
```

Whether it is a "decoded passthrough" packet (carrying `DecodedFrameResource`).
The decoder then proceeds with zero copies to the branch, skipping its own decoding.

### _disposed

```csharp
public bool IsDisposed => _disposed;
```

Whether it has been released.

### Constructor

```csharp
public MediaPacket(int trackIndex, ReadOnlyMemory<byte> data,
    TimeSpan timestamp, TimeSpan duration, bool keyFrame,
    IDisposable? dataOwner = null,
    int width = 0, int height = 0, int stride = 0,
    int sampleRate = 0, int channels = 0, SampleFormat format = default,
    IFrameResource? decodedFrameResource = null);
```

Initialize a new instance of `MediaPacket`.

**Parameters**

- `trackIndex`: Index of the assigned track.
- `data`: Compress data (managed copies or zero-copy native views).
- `timestamp`: Timestamp (PTS).
- `duration`: Packet duration.
- `keyFrame`: Whether keyframes are available.
- `dataOwner`: zero-copy owner (optional). When not null, &lt;paramref name="data"/&gt; maps native reference count buffer,
&lt;see cref="Dispose"/&gt; Release the owner to decrease the native reference count by one.
- `width`: Decode frame width (for pass-through packet, default is 0).
- `height`: Decode frame height (for pass-through packet, default is 0).
- `stride`: Number of bytes for decoding frame line spans (used for pass-through packet, default is 0).
- `sampleRate`: Audio sampling rate (for pass-through packets, default 0).
- `channels`: Number of audio channels (for pass-through packet, default 0).
- `format`: Audio sampling format (for passthrough packet, default default).
- `decodedFrameResource`: Decoded frame resource (optional). The integrated depackaging + decoding backend (MF SourceReader hardware decoding / VLC) passes GPU textures or software frame resources,
Owned by this packet unless the decoder calls &lt;see cref="TakeDecodedFrameResource"/&gt; to transfer ownership.

### TakeDecodedFrameResource

```csharp
public IFrameResource? TakeDecodedFrameResource()
    => Interlocked.Exchange(ref _decodedFrameResource, null);
```

Take `DecodedFrameResource` and transfer ownership: after the call, this packet no longer holds the resource,
`Dispose` also does not release it; the caller is responsible for the final release (usually held by `VideoFrame`).

Thread safety: Use `Interlocked` to swap, and repeated calls only return non-null the first time.

**Returns**: Original resources; If this packet does not carry decoding frame resources or has been removed, return `null`.

### Dispose

```csharp
public void Dispose();
```

Release the underlying buffer (zero-copy path: native reference count minus one) and untaken decoding frame resources.
