---
title: "ISubtitleDecoder"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/ISubtitleDecoder.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ISubtitleDecoder", "DecodeAsync", "FlushAsync", "Reset", "SubtitleCodec"]
lang: "zh-CN"
---
# ISubtitleDecoder

源码: [`ISubtitleDecoder.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/ISubtitleDecoder.cs)

字幕解码器接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface ISubtitleDecoder : IMediaComponent
```

## 备注

不需要参数化 Initialize 方法——编解码信息由工厂 Create(MediaTrack) 时

通过 track.SubtitleCodec 预置到解码器内部。

生命周期由 InitializeAsync / Dispose / DisposeAsync 管理（继承自 IMediaComponent）。

## 成员

### DecodeAsync

```csharp
ValueTask<SubtitleFrame?> DecodeAsync(MediaPacket packet);
```

解码一个字幕数据包。无 CancellationToken（热路径）。

返回 `null` 表示**本次未产出字幕**（数据不足以成条、该包不含可显示内容，
或该包解码失败可跳过），**不代表**解码器失效——调用方不得据此判定整体失败或丢弃解码器
（与 `DecodeAsync` 的 null 语义一致）。

**参数**

- `packet`：待解码的字幕数据包。

**返回**：解码出的字幕帧；返回 `null` 的语义见 remarks。

### FlushAsync

```csharp
ValueTask<SubtitleFrame?> FlushAsync();
```

刷新内部缓冲，取出剩余字幕。

**返回**：缓冲中剩余的一个字幕帧；返回 `null` 表示缓冲已取尽。

### Reset

```csharp
void Reset();
```

重置解码器状态（Seek 后调用，清空字幕内部缓冲）。

### SubtitleCodec

```csharp
SubtitleCodec SubtitleCodec { get; }
```

字幕编解码器（由工厂 Create 时预置）。
