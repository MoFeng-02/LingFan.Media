---
title: "AudioCodec"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/AudioCodec.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioCodec", "AAC", "MP3", "Opus", "FLAC", "Vorbis", "PCM", "AC3", "Unknown"]
lang: "zh-CN"
---
# AudioCodec

源码: [`AudioCodec.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/AudioCodec.cs)

音频编解码器类型。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum AudioCodec : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `AAC` | AAC。 |
| `MP3` | MP3。 |
| `Opus` | Opus。 |
| `FLAC` | FLAC。 |
| `Vorbis` | Vorbis。 |
| `PCM` | PCM（无压缩原始音频）。 |
| `AC3` | AC-3 / Dolby Digital。 |
| `Unknown` | 未知编解码器。 |
