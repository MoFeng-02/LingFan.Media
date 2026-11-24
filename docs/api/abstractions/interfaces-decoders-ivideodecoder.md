---
title: "IVideoDecoder"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IVideoDecoder.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoDecoder", "Initialize", "DecodeAsync", "FlushAsync", "Reset", "Codec", "IsHardwareAccelerated"]
lang: "en-US"
---
# IVideoDecoder

Source: [`IVideoDecoder.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IVideoDecoder.cs)

Video decoder interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoDecoder : IMediaComponent
```

## Remarks

Thread safety: single-threaded use (pipeline threads), non-thread safe.

Hotpath methods (DecodeAsync/FlushAsync) do not have CancellationToken.

## Members

### Initialize

```csharp
void Initialize(VideoCodec codec, VideoSettings settings);
```

Parametric configuration: Find and open codecs. Non-lifecycle approach.

**Parameters**

- `codec`: Video codec.
- `settings`: Video configuration (including streaming time base and decoding options).

### DecodeAsync

```csharp
ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet);
```

Decode a data packet. No CancellationToken (hot path).

**Parameters**

- `packet`: Packets to be decoded.

**Returns**: Decoded video frames; Returning `null` indicates that more data is needed (such as B-frame delay).

### FlushAsync

```csharp
ValueTask<VideoFrame?> FlushAsync();
```

Refresh the internal buffer and remove the remaining frames.

**Returns**: One remaining video frame in the buffer; Returning `null` indicates that the buffer has been exhausted.

### Reset

```csharp
void Reset();
```

Reset decoder state (called after Seek).

### Codec

```csharp
VideoCodec Codec { get; }
```

Current codec.

### IsHardwareAccelerated

```csharp
bool IsHardwareAccelerated { get; }
```

Whether hardware acceleration is used.
