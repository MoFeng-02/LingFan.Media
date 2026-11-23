---
title: "SubtitleCodec"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/SubtitleCodec.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SubtitleCodec", "SRT", "ASS", "PGS", "VobSub", "WebVTT", "Unknown"]
lang: "zh-CN"
---
# SubtitleCodec

源码: [`SubtitleCodec.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/SubtitleCodec.cs)

字幕编解码器类型。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum SubtitleCodec : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `SRT` | SubRip（.srt）。 |
| `ASS` | Advanced SubStation Alpha（.ass）。 |
| `PGS` | PGS 图形字幕（位图，当前未实现）。 |
| `VobSub` | DVD VobSub 图形字幕（位图，当前未实现）。 |
| `WebVTT` | WebVTT（.vtt）。 |
| `Unknown` | 未知字幕编解码器。 |
