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

字幕解码器工厂接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface ISubtitleDecoderFactory
```

## Remarks

与 IVideoDecoderFactory / IAudioDecoderFactory 对称：

工厂按流（MediaTrack）创建对应的 ISubtitleDecoder 实例。

Singleton 工厂，每次 Create() 返回新实例。

优先使用 `CreateAsync`（对称一致性，未来网络字幕加载 I/O，支持 CT）。

## Members

### Create

```csharp
ISubtitleDecoder Create(MediaTrack track);
```

根据字幕轨道创建解码器实例（按 track.SubtitleCodec 预置）。

**Parameters**

- `track`: 字幕轨道。

**Returns**: 新创建的字幕解码器实例（编解码器已按轨道预置）。

### CreateAsync

```csharp
Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default);
```

异步根据字幕轨道创建解码器实例（按 track.SubtitleCodec 预置）。

**Parameters**

- `track`: 字幕轨道。
- `ct`: 取消令牌。

**Returns**: 新创建的字幕解码器实例。
