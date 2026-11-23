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

视频解码器工厂接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoDecoderFactory
```

## Remarks

Singleton 工厂，无状态。每次 Create() 返回新实例。

优先使用 `CreateAsync`（硬解 GPU 设备初始化可能 I/O，支持 CT）。

## Members

### Create

```csharp
IVideoDecoder Create(VideoCodec codec, VideoSettings settings);
```

根据编解码器和设置创建解码器实例。

**Parameters**

- `codec`: 视频编解码器。
- `settings`: 视频设置。

**Returns**: 新创建的视频解码器实例（未初始化，须调用 `Initialize`）。

### CreateAsync

```csharp
Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default);
```

异步根据编解码器和设置创建解码器实例。

**Parameters**

- `codec`: 视频编解码器。
- `settings`: 视频设置。
- `ct`: 取消令牌。

**Returns**: 新创建的视频解码器实例。
