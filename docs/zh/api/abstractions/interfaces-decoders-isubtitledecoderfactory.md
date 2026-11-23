---
title: "ISubtitleDecoderFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/ISubtitleDecoderFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ISubtitleDecoderFactory", "Create", "CreateAsync"]
lang: "zh-CN"
---
# ISubtitleDecoderFactory

源码: [`ISubtitleDecoderFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/ISubtitleDecoderFactory.cs)

字幕解码器工厂接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface ISubtitleDecoderFactory
```

## 备注

与 IVideoDecoderFactory / IAudioDecoderFactory 对称：

工厂按流（MediaTrack）创建对应的 ISubtitleDecoder 实例。

Singleton 工厂，每次 Create() 返回新实例。

优先使用 `CreateAsync`（对称一致性，未来网络字幕加载 I/O，支持 CT）。

## 成员

### Create

```csharp
ISubtitleDecoder Create(MediaTrack track);
```

根据字幕轨道创建解码器实例（按 track.SubtitleCodec 预置）。

### CreateAsync

```csharp
Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default);
```

异步根据字幕轨道创建解码器实例（按 track.SubtitleCodec 预置）。

**参数**

- `track`：字幕轨道。
- `ct`：取消令牌。
