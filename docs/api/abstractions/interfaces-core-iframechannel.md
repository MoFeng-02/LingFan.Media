---
title: "IFrameChannel"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IFrameChannel.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFrameChannel", "Subscribe", "Unsubscribe", "Emit"]
lang: "en-US"
---
# IFrameChannel

Source: [`IFrameChannel.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IFrameChannel.cs)

统一视频帧投递通道：解码产出 → 通道 → 扇出至所有订阅的 Sink。
有头与无头共用此通道；零拷贝是 Sink 能力差异（GPU 纹理帧 vs CPU 帧），而非独立分支。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFrameChannel
```

## Remarks

公开事件 `IMediaPlayer.VideoFrameAvailable` 即本通道的 `Action{VideoFrame}` 适配外观；
高级消费者（录制、缩略图等）可直接实现 `IFrameSink` 经本通道订阅，无需委托。

**帧所有权**：`Emit` 由管线在 `try` 内调用，
其后 `finally` 中由管线 `ReturnFrame` 释放帧——**通道与所有 Sink 均为只读借用，绝不 Dispose**。
任一 Sink 在 `OnFrame` 内 Dispose 会在多播下破坏后续订阅方，属硬违例。

## Members

### Subscribe

```csharp
IDisposable Subscribe(IFrameSink sink);
```

订阅一个 Sink，返回取消订阅的句柄（Dispose 即退订）。

### Unsubscribe

```csharp
void Unsubscribe(IFrameSink sink);
```

退订一个 Sink（按引用/相等性匹配）。

### Emit

```csharp
void Emit(VideoFrame frame);
```

向所有订阅者投递一帧。
