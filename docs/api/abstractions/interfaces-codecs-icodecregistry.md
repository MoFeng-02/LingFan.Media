---
title: "ICodecRegistry"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Codecs/ICodecRegistry.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ICodecRegistry", "IsCodecSupported", "GetDefaultVideoCodec", "GetDefaultAudioCodec"]
lang: "en-US"
---
# ICodecRegistry

Source: [`ICodecRegistry.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Codecs/ICodecRegistry.cs)

Codec registry contract (cross-layer sharing, zero external references).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface ICodecRegistry
```

## Remarks

Provides static mapping queries for container format → codecs for automatic backend selection
(`MediaOptions.EnableAutoBackendSelection`) and ability detection for use.

Pure memory static table, fully synchronized: no I/O, no await, implements return to normal `bool` /
Can be empty enumerated and never wrapped as `Task` (otherwise it is pseudo-asynchronous).

Only reference cross-level enumerations `ContainerFormat` / `VideoCodec` / `AudioCodec`
(All are types already in Abstractions), zero external references, dependency inversion strictly enforced.

## Members

### IsCodecSupported

```csharp
bool IsCodecSupported(ContainerFormat container, VideoCodec videoCodec);
```

Determine whether the specified container format supports the given video codec.

**Parameters**

- `container`: Container format.
- `videoCodec`: Video codec.

**Returns**: If supported, return `true`.

### IsCodecSupported

```csharp
bool IsCodecSupported(ContainerFormat container, AudioCodec audioCodec);
```

Determines whether the specified container format supports the given audio codec.

**Parameters**

- `container`: Container format.
- `audioCodec`: Audio codec.

**Returns**: If supported, return `true`.

### GetDefaultVideoCodec

```csharp
VideoCodec? GetDefaultVideoCodec(ContainerFormat container);
```

Gets the default video codec for the specified container format (unknown container returns `null`).

**Parameters**

- `container`: Container format.

**Returns**: Default video codec, or `null`.

### GetDefaultAudioCodec

```csharp
AudioCodec? GetDefaultAudioCodec(ContainerFormat container);
```

Get the default audio codec for the specified container format (unknown container returns `null`).

**Parameters**

- `container`: Container format.

**Returns**: Default audio codec, or `null`.
