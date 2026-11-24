---
title: "IVideoDecoderFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IVideoDecoderFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoDecoderFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# IVideoDecoderFactory

Source: [`IVideoDecoderFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IVideoDecoderFactory.cs)

Video decoder factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoDecoderFactory
```

## Remarks

Singleton factory, statusless. Each time Create() returns a new instance.

Prioritize using `CreateAsync` (hardware decoded GPU device initialization possible I/O, supports CT).

## Members

### Create

```csharp
IVideoDecoder Create(VideoCodec codec, VideoSettings settings);
```

Create a decoder instance based on the codec and settings.

**Parameters**

- `codec`: Video codec.
- `settings`: Video settings.

**Returns**: Newly created video decoder instance (not initialized, requires calling `Initialize`).

### CreateAsync

```csharp
Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default);
```

Asynchronously create decoder instances based on codecs and settings.

**Parameters**

- `codec`: Video codec.
- `settings`: Video settings.
- `ct`: Cancel token.

**Returns**: Newly created video decoder instance.
