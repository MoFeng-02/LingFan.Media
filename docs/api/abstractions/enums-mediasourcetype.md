---
title: "MediaSourceType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaSourceType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaSourceType", "File", "Network", "Stream"]
lang: "en-US"
---
# MediaSourceType

Source: [`MediaSourceType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaSourceType.cs)

Type of media source.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum MediaSourceType : int
```

## Values

| Value | Description |
| --- | --- |
| `File` | Local files. |
| `Network` | Network flow. |
| `Stream` | Custom flow. |
