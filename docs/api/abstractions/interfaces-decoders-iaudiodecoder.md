---
title: "IAudioDecoder"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoder.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioDecoder", "Initialize", "DecodeAsync", "FlushAsync", "Reset", "Codec", "OutputSampleRate", "OutputChannels"]
lang: "en-US"
---
# IAudioDecoder

Source: [`IAudioDecoder.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoder.cs)

Audio decoder interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioDecoder : IMediaComponent
```

## Remarks

Single-threaded use (pipeline threads), non-thread safe. The hot path method does not have a CancellationToken.

## Members

### Initialize

```csharp
void Initialize(AudioCodec codec, AudioSettings settings);
```

Parametric configuration: Find and open codecs.

**Parameters**

- `codec`: Audio codec.
- `settings`: Audio configuration (including stream time base and optional resampling target format).

### DecodeAsync

```csharp
ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet);
```

Decode a data packet. No CancellationToken (hot path).

**Parameters**

- `packet`: Packets to be decoded.

**Returns**: Decoded audio frames; Return `null` indicates **no audio frames produced this time**
(If more data is needed, the stream is already finished, or the packet decoding fails, it can be skipped), ** does not mean the decoder is invalid—
The caller must not use this to determine the entire failure or discard the decoder (consistent with the null semantics of `DecodeAsync`).

### FlushAsync

```csharp
ValueTask<AudioFrame?> FlushAsync();
```

Refresh the internal buffer and remove the remaining frames.

**Returns**: One remaining audio frame in the buffer; Returning `null` indicates that the buffer has been exhausted.

### Reset

```csharp
void Reset();
```

Reset decoder state (called after Seek).

### Codec

```csharp
AudioCodec Codec { get; }
```

Current codec.

### OutputSampleRate

```csharp
int OutputSampleRate { get; }
```

Decoder's actual output sampling rate (source sampling rate, or resampled target sampling rate).

Created by `Initialize`, MediaPlayer initializes the audio output device accordingly (WASAPI opens the device at a fixed rate).

**Timing**: `Initialize` After returning, **this value **is allowed to be 0**, indicating that the stream cannot be determined at this moment
(For example, AAC/MP3 requires reading the first packet before determining the sampling rate). 0 is a valid state, not an error code, and does not mean decoding failed.
Nor should one use this as a basis to make any abnormalities.

**Fulfillment obligation**: Must be before **first audio frame output** (i.e., first `DecodeAsync` returned).
Before non-null frames, the original value is backfilled to positive. If audio frames are handed over before backfilling occurs, it constitutes a breach of contract.

**Consumer obligation**: When `Initialize` returns and reads 0, **must **delay initialization itself**
Output device, initialize only after the first audio frame arrives and the value is confirmed—directly passing 0 to the output device will trigger it
`ArgumentOutOfRangeException` (output devices must be turned on at a legal fixed rate).

### OutputChannels

```csharp
int OutputChannels { get; }
```

The actual number of channels output by the decoder (source channels, or resampling target channels).

The timing is exactly the same as `OutputSampleRate`: `Initialize` returns and allows 0
(At this point, it cannot be determined; 0 is a valid state), and the backfill must be positive before the first audio frame is generated; The consumer must read 0
Delaying the initialization of the output device on its own cannot be considered a failure based on this.
