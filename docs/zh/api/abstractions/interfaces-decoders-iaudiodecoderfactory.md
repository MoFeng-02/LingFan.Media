---
title: "IAudioDecoderFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoderFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioDecoderFactory", "Create", "CreateAsync"]
lang: "zh-CN"
---
# IAudioDecoderFactory

源码: [`IAudioDecoderFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoderFactory.cs)

音频解码器工厂接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioDecoderFactory
```

## 备注

Singleton 工厂，无状态。每次 Create() 返回新实例。

优先使用 `CreateAsync`（对称一致性，支持 CT）。

## 成员

### Create

```csharp
IAudioDecoder Create(AudioCodec codec, AudioSettings settings);
```

根据编解码器和设置创建解码器实例。

### CreateAsync

```csharp
Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default);
```

异步根据编解码器和设置创建解码器实例。

**参数**

- `codec`：音频编解码器。
- `settings`：音频设置。
- `ct`：取消令牌。
