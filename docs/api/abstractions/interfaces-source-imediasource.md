---
title: "IMediaSource"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaSource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaSource", "Type", "Identifier"]
lang: "en-US"
---
# IMediaSource

Source: [`IMediaSource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaSource.cs)

媒体来源描述。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaSource
```

## Members

### Type

```csharp
MediaSourceType Type { get; }
```

来源类型。

### Identifier

```csharp
string Identifier { get; }
```

标识符（文件路径或 URL）。
