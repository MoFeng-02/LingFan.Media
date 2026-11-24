---
title: "AudioSettings"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/AudioSettings.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioSettings", "PreferredCodec", "SourceSampleRate", "SourceChannels", "OutputSampleRate", "OutputChannels", "OutputSampleFormat", "CodecConfiguration", "TimeBase"]
lang: "en-US"
---
# AudioSettings

Source: [`AudioSettings.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/AudioSettings.cs)

Audio decoding and output settings.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class AudioSettings
```

## Members

### PreferredCodec

```csharp
public AudioCodec? PreferredCodec { get; init; }
```

Preferred codec (null means automatic selection).

### SourceSampleRate

```csharp
public int? SourceSampleRate { get; init; }
```

Source sampling rate (Hz, from orbital information; null means unknown).
For decoder backends that require explicit parameters (such as the `configure` partial decoder in Android MediaCodec).
Explicit `sample-rate`/`channel-count` is required; only insufficient derivation of csd-0 returns EINVAL).

### SourceChannels

```csharp
public int? SourceChannels { get; init; }
```

Number of source channels (from track information; null means unknown). See `SourceSampleRate`.

### OutputSampleRate

```csharp
public int? OutputSampleRate { get; init; }
```

Output sampling rate (null indicates using source sampling rate).

### OutputChannels

```csharp
public int? OutputChannels { get; init; }
```

Number of output channels (null indicates number of source channels used).

### OutputSampleFormat

```csharp
public SampleFormat? OutputSampleFormat { get; init; }
```

Output sample format (null indicates using source format).

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; init; }
```

Codec private configuration (such as AAC's AudioSpecificConfig). Transmitted from orbit extradata,
The decoder sets ctx-&gt;extradata accordingly. If none, it is default to null.

### TimeBase

```csharp
public Rational TimeBase { get; init; }
```

Flow time base (`AVStream.time_base`). Transmitted from orbital information for FFmpeg backend writing
`pkt_timebase` Correctly convert timestamps. Default `default` (invalid).
