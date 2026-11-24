---
title: "IAudioDecoderFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoderFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioDecoderFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# IAudioDecoderFactory

Source: [`IAudioDecoderFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoderFactory.cs)

Audio decoder factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioDecoderFactory
```

## Remarks

Singleton factory, statusless. Each time Create() returns a new instance.

Prioritize using `CreateAsync` (symmetric consistency, CT supported).

## Members

### Create

```csharp
IAudioDecoder Create(AudioCodec codec, AudioSettings settings);
```

Create a decoder instance based on the codec and settings.

**Parameters**

- `codec`: Audio codec.
- `settings`: Audio settings.

**Returns**: Newly created audio decoder instance (not initialized, requires calling `Initialize`).

### CreateAsync

```csharp
Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default);
```

Asynchronously create decoder instances based on codecs and settings.

**Parameters**

- `codec`: Audio codec.
- `settings`: Audio settings.
- `ct`: Cancel token.

**Returns**: Newly created audio decoder instance.
