---
title: "MediaMetadata"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/MediaMetadata.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaMetadata", "Title", "Artist", "Album", "Year", "Genre", "Duration", "ContainerFormat", "ExtraFields"]
lang: "en-US"
---
# MediaMetadata

Source: [`MediaMetadata.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/MediaMetadata.cs)

媒体元数据。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaMetadata
```

## Members

### Title

```csharp
public string? Title { get; init; }
```

标题。

### Artist

```csharp
public string? Artist { get; init; }
```

艺术家。

### Album

```csharp
public string? Album { get; init; }
```

专辑。

### Year

```csharp
public int? Year { get; init; }
```

年份。

### Genre

```csharp
public string? Genre { get; init; }
```

类型/流派。

### Duration

```csharp
public TimeSpan Duration { get; init; }
```

总时长。

### ContainerFormat

```csharp
public ContainerFormat ContainerFormat { get; init; }
```

容器格式。

### ExtraFields

```csharp
public IReadOnlyDictionary<string, string> ExtraFields { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;
```

自定义字段。
