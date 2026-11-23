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

音频解码器工厂接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioDecoderFactory
```

## Remarks

Singleton 工厂，无状态。每次 Create() 返回新实例。

优先使用 `CreateAsync`（对称一致性，支持 CT）。

## Members

### Create

```csharp
IAudioDecoder Create(AudioCodec codec, AudioSettings settings);
```

根据编解码器和设置创建解码器实例。

**Parameters**

- `codec`: 音频编解码器。
- `settings`: 音频设置。

**Returns**: 新创建的音频解码器实例（未初始化，须调用 `Initialize`）。

### CreateAsync

```csharp
Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default);
```

异步根据编解码器和设置创建解码器实例。

**Parameters**

- `codec`: 音频编解码器。
- `settings`: 音频设置。
- `ct`: 取消令牌。

**Returns**: 新创建的音频解码器实例。
