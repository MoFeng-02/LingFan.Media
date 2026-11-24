---
title: "ISubtitleDecoderFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/ISubtitleDecoderFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ISubtitleDecoderFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# ISubtitleDecoderFactory

Source: [`ISubtitleDecoderFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/ISubtitleDecoderFactory.cs)

Subtitle decoder factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface ISubtitleDecoderFactory
```

## Remarks

Symmetric with IVideoDecoderFactory / IAudioDecoderFactory:

The factory creates the corresponding ISubtitleDecoder instance by stream (MediaTrack).

Singleton factory, each time Create() returns a new instance.

Prioritize using `CreateAsync` (symmetric consistency, future network subtitle loading I/O, supports CT).

## Members

### Create

```csharp
ISubtitleDecoder Create(MediaTrack track);
```

Create a decoder instance based on the subtitle track (press track. SubtitleCodec preset).

**Parameters**

- `track`: Subtitle track.

**Returns**: Newly created subtitle decoder instance (codec preset by track).

### CreateAsync

```csharp
Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default);
```

Asynchronously create decoder instances based on subtitle tracks (by track. SubtitleCodec preset).

**Parameters**

- `track`: Subtitle track.
- `ct`: Cancel token.

**Returns**: Newly created subtitle decoder instance.
