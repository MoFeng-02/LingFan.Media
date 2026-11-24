---
title: "VideoTrackInfo"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/VideoTrackInfo.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VideoTrackInfo", "Width", "Height", "FrameRate", "PixelFormat", "ColorSpace", "Sar", "Duration", "CodecConfiguration", "TimeBase"]
lang: "en-US"
---
# VideoTrackInfo

Source: [`VideoTrackInfo.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/VideoTrackInfo.cs)

Video track details.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VideoTrackInfo
```

## Members

### Width

```csharp
public int Width { get; init; }
```

Video width (pixels).

### Height

```csharp
public int Height { get; init; }
```

Video height (pixels).

### FrameRate

```csharp
public float FrameRate { get; init; }
```

Frame rate (FPS).

### PixelFormat

```csharp
public PixelFormat PixelFormat { get; init; }
```

Pixel format.

### ColorSpace

```csharp
public string? ColorSpace { get; init; }
```

Color space (may be null).

### Sar

```csharp
public Rational? Sar { get; init; }
```

Sample aspect ratio (may be null).

### Duration

```csharp
public TimeSpan Duration { get; init; }
```

Orbital duration.

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; set; }
```

Codec private configuration (SPS+PPS / avcC / hvcC on H264/H265). extracted from track media type by the unwrapper,
Transmitted to the decoder (MF backend via `MF_MT_MPEG_SEQUENCE_HEADER`). The contract layer evolves with a "only increase, no changes" approach, zero external references.
Using readable and writable properties (not init): The unwrapper backfills ParseTracks after track construction.

### TimeBase

```csharp
public Rational TimeBase { get; set; }
```

Flow time base (`AVStream.time_base`). The pts/dts of ffmpeg decoding frames are used as units and must be transmitted to the decoder
Write `AVCodecContext.pkt_timebase` to correctly convert the timestamp. After decoding `avFrame->time_base` /
`ctx->time_base` is often 0 and cannot be used directly for conversion (otherwise, all frame timestamps are 0).
