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

音频解码与输出设置。

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

首选编解码器（null 表示自动选择）。

### SourceSampleRate

```csharp
public int? SourceSampleRate { get; init; }
```

源采样率（Hz，来自轨道信息；null 表示未知）。
供需要显式参数的解码后端使用（如 Android MediaCodec 的 `configure` 部分解码器
要求显式 `sample-rate`/`channel-count`，仅 csd-0 推导不足会返回 EINVAL）。

### SourceChannels

```csharp
public int? SourceChannels { get; init; }
```

源声道数（来自轨道信息；null 表示未知）。见 `SourceSampleRate`。

### OutputSampleRate

```csharp
public int? OutputSampleRate { get; init; }
```

输出采样率（null 表示使用源采样率）。

### OutputChannels

```csharp
public int? OutputChannels { get; init; }
```

输出声道数（null 表示使用源声道数）。

### OutputSampleFormat

```csharp
public SampleFormat? OutputSampleFormat { get; init; }
```

输出采样格式（null 表示使用源格式）。

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; init; }
```

编解码器私有配置（如 AAC 的 AudioSpecificConfig）。透传自轨道 extradata，
解码器据此设置 ctx-&gt;extradata。无则为默认空。

### TimeBase

```csharp
public Rational TimeBase { get; init; }
```

流时间基（`AVStream.time_base`）。透传自轨道信息，供 FFmpeg 后端写入
`pkt_timebase` 以正确换算时间戳。默认 `default`（无效）。
