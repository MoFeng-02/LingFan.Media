---
title: "SubtitleCodec"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/SubtitleCodec.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SubtitleCodec", "SRT", "ASS", "PGS", "VobSub", "WebVTT", "Unknown"]
lang: "en-US"
---
# SubtitleCodec

Source: [`SubtitleCodec.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/SubtitleCodec.cs)

Subtitle codec type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum SubtitleCodec : int
```

## Values

| Value | Description |
| --- | --- |
| `SRT` | SubRip（.srt）。 |
| `ASS` | Advanced SubStation Alpha（.ass）。 |
| `PGS` | PGS graphic subtitles (bitmaps, currently not implemented). |
| `VobSub` | DVD VobSub graphic subtitles (bitmaps, currently not implemented). |
| `WebVTT` | WebVTT（.vtt）。 |
| `Unknown` | Unknown subtitle codec. |
