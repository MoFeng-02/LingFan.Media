---
title: "AudioFrame"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/AudioFrame.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioFrame", "Data", "SampleRate", "Channels", "SampleFormat", "Timestamp", "Duration", "FrameCount", "IsDisposed", "Reset", "Dispose"]
lang: "en-US"
---
# AudioFrame

Source: [`AudioFrame.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/AudioFrame.cs)

Audio frame. Implement `IDisposableFrame`.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class AudioFrame : IDisposableFrame
```

## Remarks

Span access mode (document notes):

```csharp
ReadOnlySpan&lt;float&gt; samples = MemoryMarshal.Cast&lt;byte, float&gt;(frame. Data.Span);
ReadOnlySpan&lt;short&gt; samples = MemoryMarshal.Cast&lt;byte, short&gt;(frame. Data.Span);
```

The property uses the internal set,`Reset` method for the decoder to reuse frame instances (framepooling).

**zero-copy support**: `Data` Can directly map native reference counts to buffer,
The owner enters via neutral `IDisposable` via `Reset`;
Both `Dispose` and the next `Reset` release the owner (native reference count minus one).
Once the owner releases it, access to `Data` is prohibited.

## Members

### Data

```csharp
public ReadOnlyMemory<byte> Data { get; internal set; }
```

PCM audio data (read-only view, underlying buffer owned by frames or shared via reference count).

### SampleRate

```csharp
public int SampleRate { get; internal set; }
```

Sampling rate (Hz, e.g., 44100).

### Channels

```csharp
public int Channels { get; internal set; }
```

Number of sound channels.

### SampleFormat

```csharp
public SampleFormat SampleFormat { get; internal set; }
```

Sampling format.

### Timestamp

```csharp
public TimeSpan Timestamp { get; internal set; }
```

Timestamp.

### Duration

```csharp
public TimeSpan Duration { get; internal set; }
```

Frame duration.

### FrameCount

```csharp
public int FrameCount { get; internal set; }
```

Number of samples contained in this frame.

### IsDisposed

```csharp
public bool IsDisposed { get; private set; }
```

Inherits documentation from its base contract.

### Constructor

```csharp
public AudioFrame(ReadOnlyMemory<byte> data, int sampleRate, int channels,
    SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount);
```

Initialize a new instance of `AudioFrame`.

**Parameters**

- `data`: PCM audio data.
- `sampleRate`: Sampling rate (Hz).
- `channels`: Number of sound channels.
- `sampleFormat`: Sampling format.
- `timestamp`: Timestamp.
- `duration`: Frame duration.
- `frameCount`: Number of samples contained in this frame.

### Constructor

```csharp
public AudioFrame();
```

Non-parameter constructor (for FramePool factory to create empty shells).

For use only by frame object pools. The caller must populate the actual data using `Reset`.

### Reset

```csharp
public void Reset(ReadOnlyMemory<byte> data, int sampleRate, int channels,
    SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount,
    IDisposable? dataOwner = null);
```

Resets frame state for reuse by FramePool.
Release the old zero-copy owner (if any), set the new property value, and reset IsDisposed.

**Parameters**

- `data`: PCM data (managed copy or zero-copy native view).
- `sampleRate`: Sampling rate.
- `channels`: Number of sound channels.
- `sampleFormat`: Sampling format.
- `timestamp`: Timestamp.
- `duration`: Frame duration.
- `frameCount`: Number of samples.
- `dataOwner`: zero-copy owner (optional). When not null, &lt;paramref name="data"/&gt; maps native reference count buffer.

### Dispose

```csharp
public void Dispose();
```

Release the underlying buffer (zero-copy path: native reference count minus one).
