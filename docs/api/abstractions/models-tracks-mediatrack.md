---
title: "MediaTrack"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/MediaTrack.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaTrack", "Index", "Type", "VideoCodec", "AudioCodec", "SubtitleCodec", "Language", "Title", "IsDefault", "BitRate", "VideoInfo", "AudioInfo"]
lang: "en-US"
---
# MediaTrack

Source: [`MediaTrack.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/MediaTrack.cs)

Media track information.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaTrack
```

## Remarks

Codec field split into `VideoCodec` / `AudioCodec` / `SubtitleCodec`
Three independent empty attributes, no generics needed.

## Members

### Index

```csharp
public int Index { get; init; }
```

Track index (stream index within the container).

### Type

```csharp
public TrackType Type { get; init; }
```

Track type.

### VideoCodec

```csharp
public VideoCodec? VideoCodec { get; init; }
```

Video codec (valid only when Type=Video, otherwise null).

### AudioCodec

```csharp
public AudioCodec? AudioCodec { get; init; }
```

Audio codec (valid only when Type=Audio, otherwise null).

### SubtitleCodec

```csharp
public SubtitleCodec? SubtitleCodec { get; init; }
```

Subtitle codec (valid only when Type=Subtitle, otherwise null).

### Language

```csharp
public string? Language { get; init; }
```

Language tags (such as "en", "zh", which may be null).

### Title

```csharp
public string? Title { get; init; }
```

Track title (may be null).

### IsDefault

```csharp
public bool IsDefault { get; init; }
```

Whether to default tracks.

### BitRate

```csharp
public long BitRate { get; init; }
```

Bit rate (bps).

### VideoInfo

```csharp
public VideoTrackInfo? VideoInfo { get; init; }
```

Video details (only valid for video tracks, otherwise null).

### AudioInfo

```csharp
public AudioTrackInfo? AudioInfo { get; init; }
```

Audio details (only audio tracks are valid, otherwise null).
