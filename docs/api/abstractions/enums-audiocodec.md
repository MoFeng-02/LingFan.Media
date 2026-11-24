---
title: "AudioCodec"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/AudioCodec.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioCodec", "AAC", "MP3", "Opus", "FLAC", "Vorbis", "PCM", "AC3", "Unknown"]
lang: "en-US"
---
# AudioCodec

Source: [`AudioCodec.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/AudioCodec.cs)

Audio codec type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum AudioCodec : int
```

## Values

| Value | Description |
| --- | --- |
| `AAC` | AAC。 |
| `MP3` | MP3。 |
| `Opus` | Opus。 |
| `FLAC` | FLAC。 |
| `Vorbis` | Vorbis。 |
| `PCM` | PCM (Uncompressed Original Audio). |
| `AC3` | AC-3 / Dolby Digital。 |
| `Unknown` | Unknown codec. |
