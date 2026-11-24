---
title: "MediaFormatProfile"
type: "struct"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/MediaFormatProfile.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaFormatProfile", "Container", "Video"]
lang: "en-US"
---
# MediaFormatProfile

Source: [`MediaFormatProfile.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/MediaFormatProfile.cs)

Lightweight detection of the "container format + master video encoding" combination.
Description: pure data, no detection algorithms.
"Format-level memory" used for rollback schedulers: the same (container, encoding) combination experiences only one exception drive rollback,
Subsequent similar files directly hit the memory backend, eliminating the overhead of repeated rollbacks.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public readonly struct MediaFormatProfile
```

## Remarks

This structure is placed at the Abstractions layer, enabling high-level middleware (such as LingFan.Media.Playback to rollback schedulers)
Relies only on this data contract and `IFormatDetector` interface, without referencing specific probe implementations (LingFan.Media.Formats),
Strictly adhere to Dependency Inversion (DIP).

## Members

### Container

```csharp
public readonly ContainerFormat Container;
```

Container formats (such as MP4 / WebM).

### Video

```csharp
public readonly VideoCodec Video;
```

Primary video encoding (undetected `Unknown`).

### Constructor

```csharp
public MediaFormatProfile(ContainerFormat container, VideoCodec video);
```

Initialize the format of the composite profile.

**Parameters**

- `container`: Container format.
- `video`: Primary video encoding (if not detected, &lt;see cref="VideoCodec.Unknown"/&gt;).
