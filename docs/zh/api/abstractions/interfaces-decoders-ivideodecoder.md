---
title: "IVideoDecoder"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IVideoDecoder.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoDecoder", "Initialize", "DecodeAsync", "FlushAsync", "Reset", "Codec", "IsHardwareAccelerated"]
lang: "zh-CN"
---
# IVideoDecoder

源码: [`IVideoDecoder.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IVideoDecoder.cs)

视频解码器接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoDecoder : IMediaComponent
```

## 备注

线程安全：单线程使用（管线线程），非线程安全。

热路径方法（DecodeAsync/FlushAsync）无 CancellationToken。

## 成员

### Initialize

```csharp
void Initialize(VideoCodec codec, VideoSettings settings);
```

参数化配置：查找编解码器并打开。非生命周期方法。

### DecodeAsync

```csharp
ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet);
```

解码一个数据包。无 CancellationToken（热路径）。
返回 null 表示需要更多数据（如 B 帧延迟）。

### FlushAsync

```csharp
ValueTask<VideoFrame?> FlushAsync();
```

刷新内部缓冲，取出剩余帧。

### Reset

```csharp
void Reset();
```

重置解码器状态（Seek 后调用）。

### Codec

```csharp
VideoCodec Codec { get; }
```

当前编解码器。

### IsHardwareAccelerated

```csharp
bool IsHardwareAccelerated { get; }
```

是否使用硬件加速。
