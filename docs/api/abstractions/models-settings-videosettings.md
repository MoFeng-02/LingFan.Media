---
title: "VideoSettings"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/VideoSettings.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VideoSettings", "EnableHardwareAcceleration", "PreferredCodec", "MaxWidth", "MaxHeight", "Width", "Height", "OutputPixelFormat", "CodecConfiguration", "TimeBase"]
lang: "en-US"
---
# VideoSettings

Source: [`VideoSettings.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/VideoSettings.cs)

Video decoding and rendering settings.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VideoSettings
```

## Members

### EnableHardwareAcceleration

```csharp
public bool EnableHardwareAcceleration { get; init; } = true;
```

Whether hardware accelerated decoding is enabled.

### PreferredCodec

```csharp
public VideoCodec? PreferredCodec { get; init; }
```

Preferred codec (null means automatic selection).

### MaxWidth

```csharp
public int? MaxWidth { get; init; }
```

Maximum decoding width (null means unlimited).

### MaxHeight

```csharp
public int? MaxHeight { get; init; }
```

Maximum decoding height (null means unlimited).

### Width

```csharp
public int? Width { get; init; }
```

Source video width (pixels, from track information; null means unknown).
Used by decoding backends that require explicit width and height (such as Android MediaCodec's `configure` device/decoder).
Explicit `width`/`height` is required; only insufficient derivation of csd-0 returns EINVAL).

### Height

```csharp
public int? Height { get; init; }
```

Source video height (pixels, from track information; null means unknown).
See `Width`.

### OutputPixelFormat

```csharp
public PixelFormat? OutputPixelFormat { get; init; }
```

Output pixel format (null means using source format).

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; init; }
```

Codec private configurations (such as SPS+PPS / avcC / hvcC on H264/H265), provided by the decapsurer in the track information,
Pass-through to the decoder to set the input media type (MF backend passes through `MF_MT_MPEG_SEQUENCE_HEADER`, FFmpeg and other backends are usually ignored).
Zero external references (BCL `ReadOnlyMemory{T}`), which complies with the contract layer's "only increments, no changes" evolution criterion.

### TimeBase

```csharp
public Rational TimeBase { get; init; }
```

Flow time base (`AVStream.time_base`). Transmits track information for backends like FFmpeg to write
`AVCodecContext.pkt_timebase`, converts the decoding frame pts/dts to the correct number of seconds. Default `default` (invalid).
