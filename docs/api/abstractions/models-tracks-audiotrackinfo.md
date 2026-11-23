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

音频轨道详情信息。

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

采样率（Hz，如 44100）。

### Channels

```csharp
public int Channels { get; init; }
```

声道数。

### ChannelLayout

```csharp
public string? ChannelLayout { get; init; }
```

声道布局（如 "mono"、"stereo"、"5.1"，可能为 null）。

### BitsPerSample

```csharp
public int BitsPerSample { get; init; }
```

每采样位数。

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; init; }
```

编解码器私有配置（如 AAC 的 AudioSpecificConfig）。解码器需据此设置 extradata。
无则为默认空。

### TimeBase

```csharp
public Rational TimeBase { get; init; }
```

流时间基（`AVStream.time_base`）。透传给解码器写入 `pkt_timebase` 以正确换算时间戳。

### Duration

```csharp
public TimeSpan Duration { get; init; }
```

轨道时长。
