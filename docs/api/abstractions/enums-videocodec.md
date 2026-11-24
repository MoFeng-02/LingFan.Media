---
title: "VideoCodec"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/VideoCodec.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VideoCodec", "H264", "H265", "AV1", "VP9", "MPEG2", "MPEG4", "Unknown"]
lang: "en-US"
---
# VideoCodec

Source: [`VideoCodec.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/VideoCodec.cs)

Video codec type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum VideoCodec : int
```

## Values

| Value | Description |
| --- | --- |
| `H264` | H.264 / AVC。 |
| `H265` | H.265 / HEVC。 |
| `AV1` | AV1。 |
| `VP9` | VP9。 |
| `MPEG2` | MPEG-2。 |
| `MPEG4` | MPEG-4。 |
| `Unknown` | Unknown codec. |
