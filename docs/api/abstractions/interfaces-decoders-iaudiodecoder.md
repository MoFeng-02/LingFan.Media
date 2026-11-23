---
title: "IAudioDecoder"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoder.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioDecoder", "Initialize", "DecodeAsync", "FlushAsync", "Reset", "Codec", "OutputSampleRate", "OutputChannels"]
lang: "en-US"
---
# IAudioDecoder

Source: [`IAudioDecoder.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioDecoder.cs)

音频解码器接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioDecoder : IMediaComponent
```

## Remarks

单线程使用（管线线程），非线程安全。热路径方法无 CancellationToken。

## Members

### Initialize

```csharp
void Initialize(AudioCodec codec, AudioSettings settings);
```

参数化配置：查找编解码器并打开。

### DecodeAsync

```csharp
ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet);
```

解码一个数据包。无 CancellationToken（热路径）。

### FlushAsync

```csharp
ValueTask<AudioFrame?> FlushAsync();
```

刷新内部缓冲，取出剩余帧。

### Reset

```csharp
void Reset();
```

重置解码器状态（Seek 后调用）。

### Codec

```csharp
AudioCodec Codec { get; }
```

当前编解码器。

### OutputSampleRate

```csharp
int OutputSampleRate { get; }
```

解码器实际输出采样率（源采样率，或重采样目标采样率）。

由 `Initialize` 建立，MediaPlayer 据此初始化音频输出设备（WASAPI 以固定率开设备）。

**时序**：`Initialize` 返回后本值**允许为 0**，表示此刻尚无法从码流判定
（如 AAC/MP3 需先读到首个数据包才能确定采样率）。0 是合法状态，不是错误码、不代表解码失败，
也不得据此抛异常。

**实现义务**：必须在**首个音频帧产出前**（即首次 `DecodeAsync` 返回
非 null 帧之前）把本值回填为正。回填仍未发生就交出音频帧，属于实现违约。

**消费方义务**：在 `Initialize` 返回后读到 0 时**必须**自行延迟初始化
输出设备，待首个音频帧到达、本值确定后再初始化——把 0 直接交给输出设备会触发
`ArgumentOutOfRangeException`（输出设备必须以合法固定率打开）。

### OutputChannels

```csharp
int OutputChannels { get; }
```

解码器实际输出声道数（源声道数，或重采样目标声道数）。

时序与 `OutputSampleRate` 完全相同：`Initialize` 返回后允许为 0
（此刻尚不可判定，0 是合法状态），必须在首个音频帧产出前回填为正；消费方读到 0 时必须
自行延迟初始化输出设备，不得据此判定失败。
