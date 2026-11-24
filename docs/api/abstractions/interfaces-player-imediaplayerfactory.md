---
title: "IMediaPlayerFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayerFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaPlayerFactory", "Create"]
lang: "en-US"
---
# IMediaPlayerFactory

Source: [`IMediaPlayerFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayerFactory.cs)

Player factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaPlayerFactory
```

## Remarks

The factory itself is Singleton (stateless). Each time Create() returns a new instance (Session level, Transient).

Added `Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)` overload to explicitly specify backend groups,
For manual combination or runtime rollback by middleware/advanced consumers—this overload relies solely on contract interfaces, does not reference any specific backend, and strictly adheres to dependency inversion.

## Members

### Create

```csharp
IMediaPlayer Create();
```

Create a new IMediaPlayer instance (parse the monolithic factory registered in DI).

**Returns**: Newly created player instance (Session level, media not open).

### Create

```csharp
IMediaPlayer Create(
    IMediaDemuxerFactory demuxerFactory,
    IVideoDecoderFactory videoDecoderFactory,
    IAudioDecoderFactory audioDecoderFactory,
    ISubtitleDecoderFactory? subtitleDecoderFactory = null);
```

Create a new IMediaPlayer instance using the explicitly specified backend group factory (demuxer + video/audio/subtitle decoder).
Used for manually assembling backends (such as `IBackendRegistry` for a selected backend group), without relying on DI's default monolithic factory parsing.

**Parameters**

- `demuxerFactory`: Unpackager factory (registered backend Singleton stateless service).
- `videoDecoderFactory`: Video decoder factory.
- `audioDecoderFactory`: Audio decoder factory.
- `subtitleDecoderFactory`: Subtitle Decoder Factory (optional; Some backends do not provide subtitle decoding, sending null).

**Returns**: Use a new player instance for the specified backend group (Session level, media not open).
