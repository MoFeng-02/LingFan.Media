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

Media metadata.

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

Title.

### Artist

```csharp
public string? Artist { get; init; }
```

Artist.

### Album

```csharp
public string? Album { get; init; }
```

Album.

### Year

```csharp
public int? Year { get; init; }
```

Year.

### Genre

```csharp
public string? Genre { get; init; }
```

Genre/genre.

### Duration

```csharp
public TimeSpan Duration { get; init; }
```

Total duration.

### ContainerFormat

```csharp
public ContainerFormat ContainerFormat { get; init; }
```

Container format.

### ExtraFields

```csharp
public IReadOnlyDictionary<string, string> ExtraFields { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;
```

Custom fields.
