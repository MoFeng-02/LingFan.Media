---
title: "AudioTrackInfo"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/AudioTrackInfo.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioTrackInfo", "SampleRate", "Channels", "ChannelLayout", "BitsPerSample", "CodecConfiguration", "TimeBase", "Duration"]
lang: "en-US"
---
# AudioTrackInfo

Source: [`AudioTrackInfo.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/AudioTrackInfo.cs)

Audio track details.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class AudioTrackInfo
```

## Members

### SampleRate

```csharp
public int SampleRate { get; init; }
```

Sampling rate (Hz, e.g., 44100).

### Channels

```csharp
public int Channels { get; init; }
```

Number of sound channels.

### ChannelLayout

```csharp
public string? ChannelLayout { get; init; }
```

Channel layout (such as "mono", "stereo", "5.1", which may be null).

### BitsPerSample

```csharp
public int BitsPerSample { get; init; }
```

Number of bits per sample.

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; init; }
```

Codec private configuration (such as AAC's AudioSpecificConfig). The decoder needs to set extradata accordingly.
If none, it is default to null.

### TimeBase

```csharp
public Rational TimeBase { get; init; }
```

Flow time base (`AVStream.time_base`). Pass-through to the decoder to write `pkt_timebase` to correctly convert the timestamp.

### Duration

```csharp
public TimeSpan Duration { get; init; }
```

Orbital duration.
