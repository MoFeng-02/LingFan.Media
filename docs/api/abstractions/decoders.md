# Decoders & Codecs

## IVideoDecoder

Namespace: `LingFan.Media.Abstractions`

```csharp
public interface IVideoDecoder : IMediaComponent
{
    VideoCodec Codec { get; }
    bool IsHardwareAccelerated { get; }
    void Initialize(VideoCodec codec, VideoSettings settings);
    ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet);
}
```

| Member | Notes |
|--------|-------|
| `Codec` | The video codec this decoder handles. |
| `IsHardwareAccelerated` | Whether HW acceleration is active (e.g. D3D11VA / DXVA). |
| `Initialize(codec, settings)` | Configure (pure in-memory). |
| `DecodeAsync(packet)` | Decode one packet → a `VideoFrame` (or `null`). `ValueTask` hot path. |

## IAudioDecoder

```csharp
public interface IAudioDecoder : IMediaComponent
{
    AudioCodec Codec { get; }
    void Initialize(AudioCodec codec, AudioSettings settings);
    ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet);
}
```

Plus `IAudioSourceFormatAware` for decoders that need the source PCM format.

## ISubtitleDecoder

```csharp
public interface ISubtitleDecoder : IMediaComponent
{
    SubtitleCodec Codec { get; }
    void Initialize(SubtitleCodec codec, …);
    ValueTask<SubtitleFrame?> DecodeAsync(MediaPacket packet);
}
```

## Factories

| Factory | Creates |
|---------|---------|
| `IVideoDecoderFactory` | `IVideoDecoder` |
| `IAudioDecoderFactory` | `IAudioDecoder` |
| `ISubtitleDecoderFactory` | `ISubtitleDecoder` |

Each factory's `Create(...)` is used by the fallback middleware / composer to build a session.

## ICodecRegistry

Namespace: `LingFan.Media.Abstractions` (`Codecs/`)

```csharp
public interface ICodecRegistry
{
    bool IsCodecSupported(VideoCodec codec);
    bool IsCodecSupported(AudioCodec codec);
    VideoCodec GetDefaultVideoCodec();
    AudioCodec GetDefaultAudioCodec();
    // … overloads per media type
}
```

Answers "is codec X supported by the current backend set?" — used by the fallback scheduler.
