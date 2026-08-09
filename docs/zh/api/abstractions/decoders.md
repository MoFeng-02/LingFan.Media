# 解码器与编解码器

## IVideoDecoder

命名空间：`LingFan.Media.Abstractions`

```csharp
public interface IVideoDecoder : IMediaComponent
{
    VideoCodec Codec { get; }
    bool IsHardwareAccelerated { get; }
    void Initialize(VideoCodec codec, VideoSettings settings);
    ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet);
}
```

| 成员 | 说明 |
|--------|-------|
| `Codec` | 该解码器处理的视频编解码器。 |
| `IsHardwareAccelerated` | 是否启用了硬件加速（如 D3D11VA / DXVA）。 |
| `Initialize(codec, settings)` | 配置（纯内存）。 |
| `DecodeAsync(packet)` | 解码一个包 → 一个 `VideoFrame`（或 `null`）。`ValueTask` 热路径。 |

## IAudioDecoder

```csharp
public interface IAudioDecoder : IMediaComponent
{
    AudioCodec Codec { get; }
    void Initialize(AudioCodec codec, AudioSettings settings);
    ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet);
}
```

另有 `IAudioSourceFormatAware` 用于需要源 PCM 格式的解码器。

## ISubtitleDecoder

```csharp
public interface ISubtitleDecoder : IMediaComponent
{
    SubtitleCodec Codec { get; }
    void Initialize(SubtitleCodec codec, …);
    ValueTask<SubtitleFrame?> DecodeAsync(MediaPacket packet);
}
```

## 工厂

| 工厂 | 创建 |
|---------|---------|
| `IVideoDecoderFactory` | `IVideoDecoder` |
| `IAudioDecoderFactory` | `IAudioDecoder` |
| `ISubtitleDecoderFactory` | `ISubtitleDecoder` |

每个工厂的 `Create(...)` 由回退中间件 / 组合器用来构建会话。

## ICodecRegistry

命名空间：`LingFan.Media.Abstractions`（`Codecs/`）

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

回答"当前后端集是否支持编解码器 X？"——由回退调度器使用。
