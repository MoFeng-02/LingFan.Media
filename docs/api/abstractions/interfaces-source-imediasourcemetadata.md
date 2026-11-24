---
title: "IMediaSourceMetadata"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaSourceMetadata.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaSourceMetadata", "Name", "ContentType", "IsLive", "ExtraFields"]
lang: "en-US"
---
# IMediaSourceMetadata

Source: [`IMediaSourceMetadata.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaSourceMetadata.cs)

Media source metadata interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaSourceMetadata
```

## Members

### Name

```csharp
string? Name { get; }
```

Name (may be null).

### ContentType

```csharp
string? ContentType { get; }
```

MIME type (may be null).

### IsLive

```csharp
bool IsLive { get; }
```

Whether it is a live stream.

### ExtraFields

```csharp
IReadOnlyDictionary<string, string> ExtraFields { get; }
```

Custom fields.
