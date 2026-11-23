---
title: "MediaTrack"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/MediaTrack.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaTrack", "Index", "Type", "VideoCodec", "AudioCodec", "SubtitleCodec", "Language", "Title", "IsDefault", "BitRate", "VideoInfo", "AudioInfo"]
lang: "zh-CN"
---
# MediaTrack

源码: [`MediaTrack.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/MediaTrack.cs)

媒体轨道信息。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaTrack
```

## 备注

Codec 字段拆为 `VideoCodec` / `AudioCodec` / `SubtitleCodec`
三个独立可空属性，不用泛型。

## 成员

### Index

```csharp
public int Index { get; init; }
```

轨道索引（在容器中的流索引）。

### Type

```csharp
public TrackType Type { get; init; }
```

轨道类型。

### VideoCodec

```csharp
public VideoCodec? VideoCodec { get; init; }
```

视频编解码器（仅 Type=Video 时有效，否则 null）。

### AudioCodec

```csharp
public AudioCodec? AudioCodec { get; init; }
```

音频编解码器（仅 Type=Audio 时有效，否则 null）。

### SubtitleCodec

```csharp
public SubtitleCodec? SubtitleCodec { get; init; }
```

字幕编解码器（仅 Type=Subtitle 时有效，否则 null）。

### Language

```csharp
public string? Language { get; init; }
```

语言标签（如 "en"、"zh"，可能为 null）。

### Title

```csharp
public string? Title { get; init; }
```

轨道标题（可能为 null）。

### IsDefault

```csharp
public bool IsDefault { get; init; }
```

是否默认轨道。

### BitRate

```csharp
public long BitRate { get; init; }
```

比特率（bps）。

### VideoInfo

```csharp
public VideoTrackInfo? VideoInfo { get; init; }
```

视频详情（仅视频轨道有效，否则 null）。

### AudioInfo

```csharp
public AudioTrackInfo? AudioInfo { get; init; }
```

音频详情（仅音频轨道有效，否则 null）。
