---
title: "IBufferManager"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IBufferManager.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IBufferManager", "BufferedDuration", "BufferedBytes", "IsReady", "State", "TargetDuration", "BufferProgressChanged", "StartAsync", "Stop", "Clear"]
lang: "zh-CN"
---
# IBufferManager

源码: [`IBufferManager.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IBufferManager.cs)

缓冲管理器接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IBufferManager
```

## 备注

管理 Demuxer 到 Decoder 之间的数据包缓冲。

FrameBuffer 无 SubtitleFrameQueue——字幕帧仅含文本和时间戳，

无 GPU 资源、无需 Dispose、无实时性要求。字幕帧由 SubtitleProcessor 内部缓存管理。

## 成员

### BufferedDuration

```csharp
TimeSpan BufferedDuration { get; }
```

当前已缓存时长。

### BufferedBytes

```csharp
long BufferedBytes { get; }
```

当前缓冲大小（字节）。

### IsReady

```csharp
bool IsReady { get; }
```

是否达到可播放阈值。

### State

```csharp
BufferState State { get; }
```

当前缓冲状态。

### TargetDuration

```csharp
TimeSpan TargetDuration { get; set; }
```

目标缓冲时长。

**副作用**：setter 除更新目标时长外，还会按固定比例（目标的 40%）同步重算「就绪阈值」——
即把目标调大/调小时，判定「缓冲是否足够、可以开始播放」的门槛会随之变化，调用方无需另行设置。

该写入不触发事件、不立即重新评估就绪状态：新阈值在下一次就绪判定时生效；
运行期改动它只会影响后续判定，不会回溯已进入播放的状态。

### BufferProgressChanged

```csharp
event EventHandler<BufferProgressEventArgs>? BufferProgressChanged;
```

缓冲进度变更事件。

### StartAsync

```csharp
Task StartAsync(CancellationToken ct = default);
```

开始缓冲（预读取）。支持取消。

### Stop

```csharp
void Stop();
```

停止缓冲。

### Clear

```csharp
void Clear();
```

清空所有缓冲。
