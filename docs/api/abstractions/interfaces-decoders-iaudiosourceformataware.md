---
title: "IAudioSourceFormatAware"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioSourceFormatAware.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioSourceFormatAware", "SetSourceFormat"]
lang: "en-US"
---
# IAudioSourceFormatAware

Source: [`IAudioSourceFormatAware.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioSourceFormatAware.cs)

Source audio format-aware interface. Optional implementation: **Pass-through** audio decoder (does not decode itself, PCM already produced by the depackaging layer)
Receive the true PCM output format from the caller through this interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioSourceFormatAware
```

## Remarks

**Motivation**: `OutputSampleRate` / `OutputChannels`
It is the sole basis for MediaPlayer to initialize audio output devices (such as WASAPI to open devices with a fixed sample rate).
Backends with built-in decoder context (FFmpeg) can directly obtain source parameters from the codec context;
The **pass-through** backend (MediaFoundation: SourceReader internally completes decoding, and the decoder only wraps PCM bytes)
Parameters unknown; if the hardcoded default value (44100/2) does not match the real media→ pitch/rhythm is disordered.
This interface allows the caller to inject the true format parsed by the unwrapping layer into the interface.

**Do not modify existing interfaces** (such as IAudioDecoder), and are detected by the caller's pattern matching (`is`).
Same form as `IFramePoolAware{T}`; AOT security (compile-time type determined, no reflection).

**Call timing**: Must be after `Initialize`,
Audio output device initialization before the first `DecodeAsync` call.

**Asynchronous Strategy**: Synchronous configuration—pure memory allocation, no I/O, no async overloading.

## Members

### SetSourceFormat

```csharp
void SetSourceFormat(int sampleRate, int channels, SampleFormat sampleFormat);
```

Injection of the PCM output format tested by the depackaging layer.

**Parameters**

- `sampleRate`: Sampling rate (Hz), must be &gt; 0.
- `channels`: Number of channels, must be &gt; 0.
- `sampleFormat`: Sampling format.
