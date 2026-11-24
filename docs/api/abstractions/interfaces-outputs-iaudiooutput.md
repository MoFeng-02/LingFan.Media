---
title: "IAudioOutput"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutput.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioOutput", "Initialize", "Submit", "Pause", "Resume", "BeginStreamingAsync", "Flush", "GetPlaybackPosition", "GetPlaybackPositionDirect", "ResetPlaybackClock", "Latency", "Volume"]
lang: "en-US"
---
# IAudioOutput

Source: [`IAudioOutput.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutput.cs)

Audio output interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioOutput : IMediaComponent
```

## Remarks

Thread model: Submit in the audio thread call.

Ownership semantics: Submit does not take over frame ownership, does not Dispose frames; The caller is responsible for returning the pool or releasing after Submitting.

Implements synchronous copying of PCM data to output buffers only; The caller (AudioPipeline) is responsible for returning frames to FramePool or Dispose after Submitting.

IFrameResource is non-thread-safe and must be used within a single thread.

## Members

### Initialize

```csharp
void Initialize(int sampleRate, int channels);
```

Parametric configuration: Set the sample rate and number of channels.

**Parameters**

- `sampleRate`: Output sampling rate (Hz) must be a legal fixed rate.
- `channels`: Number of output channels.

### Submit

```csharp
void Submit(AudioFrame frame);
```

Submit the audio frame. Do not take over frame ownership; Synchronize only copy PCM data,
The caller is responsible for releasing frames after Submit (Return to pool or Dispose).
Audio thread calls block when buffering is full (COM backpressure; synchronous blocking backpressure is a normal mechanism, not pseudo-asynchronous).

**Parameters**

- `frame`: Audio frames pending submission; Ownership remains with the caller.

### Pause

```csharp
void Pause();
```

Pause playback.

### Resume

```csharp
void Resume();
```

Resume playback.

### BeginStreamingAsync

```csharp
ValueTask BeginStreamingAsync(CancellationToken ct);
```

Start stream output, but start the device clock (preroll semantics) after the buffer is prefilled with enough real PCM,
Audio has started flowing upon return. By default, playback is directly resumed (backend without pre-filling requirements, such as silent/device-free output);
WASAPI was rewritten to resolve the silent startup window (the engine fetched real data rather than mute).

**Parameters**

- `ct`: Cancel token.

**Returns**: Task ended when audio has started flowing.

### Flush

```csharp
void Flush();
```

Clear the output buffer.

### GetPlaybackPosition

```csharp
TimeSpan GetPlaybackPosition();
```

Get the playback position (for clock synchronization).

**Returns**: The duration of the audio output since this playback.

### GetPlaybackPositionDirect

```csharp
TimeSpan GetPlaybackPositionDirect() => GetPlaybackPosition();
```

High-frequency, thread-safe playback position reading (optional optimization). The default implementation falls back to `GetPlaybackPosition`.

**Purpose**: The video master clock needs to poll playback positions based on video frame rate (~30Hz). Certain backends
(WASAPI)'s `GetPlaybackPosition` cross-thread encapsulation, with high-frequency calls costly;
Such backends should override the method to directly read the device clock (`IAudioClock::GetPosition` can be called by any thread, no need to cross threads).
Smooth and free with zero cover and overhead coverage.

**Returns**: Played position; The default implementation is the same as `GetPlaybackPosition`.

### ResetPlaybackClock

```csharp
void ResetPlaybackClock() { }

/// <summary>当前输出延迟。</summary>
TimeSpan Latency { get; }

/// <summary>输出音量 (0.0~1.0)。</summary>
float Volume { get; set; }
```

Ended→Playing: Master clock reset to zero: Before the audio is played for this play Start, `GetPlaybackPositionDirect`
It should return 0 to avoid using the clock anchor from the previous playback, which could cause the first frame of replay to be misjudged as expired and discarded.
Default null implementation (no master clock backend required); WASAPI is rewritten as true disarmament logic.
