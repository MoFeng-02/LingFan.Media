---
title: "IMediaDemuxer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaDemuxer", "OpenAsync", "Tracks", "Metadata", "ReadPacketAsync", "SeekAsync", "Close"]
lang: "en-US"
---
# IMediaDemuxer

Source: [`IMediaDemuxer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxer.cs)

解封装器接口。将容器（MP4/MKV/...）拆解为独立的轨道数据包。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaDemuxer : IMediaComponent
```

## Remarks

接口定义在 Abstractions，实际实现（如 FFmpegDemuxer）在 Backends 模块。

继承 IMediaComponent，拥有 Dispose() + DisposeAsync() 两条释放路径。

线程安全：OpenAsync 后的 Tracks/Metadata 可跨线程读取；ReadPacketAsync/SeekAsync 不可并发调用。

## Members

### OpenAsync

```csharp
Task OpenAsync(IMediaStream stream, CancellationToken ct = default);
```

打开媒体流，进行格式探测和轨道解析。

**Parameters**

- `stream`: 媒体数据流。
- `ct`: 取消令牌。

**Returns**: 格式探测与轨道解析完成时结束的任务。

### Tracks

```csharp
IReadOnlyList<MediaTrack> Tracks { get; }
```

解析出的轨道列表。

### Metadata

```csharp
MediaMetadata Metadata { get; }
```

容器元数据。

### ReadPacketAsync

```csharp
ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default);
```

读取下一个数据包。返回 null 表示流结束。

**Parameters**

- `ct`: 取消令牌。

**Returns**: 下一个数据包；`null` 表示流结束。

### SeekAsync

```csharp
Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);
```

定位到指定位置。`true` 只表示「定位请求已被接受」，不保证精确落点。

**返回值语义**：`true` 表示请求被接受、后续读取从新位置继续；
容器通常只能定位到关键帧/块边界，因此**不等于**已精确落在 `position`。
需要精确落点的一方必须读回实际位置确认，或自行丢弃目标时间之前的帧。

**失败语义**：容器本身不可定位时返回 `false`，不抛异常。

**状态语义**：已释放（`Dispose` / `DisposeAsync` 之后）时调用**必须**抛
`ObjectDisposedException`。至于「已创建但尚未 `OpenAsync`」与
「`Close` 之后尚未释放」这两种状态，本接口**不统一规定**：实现可返回
`false` 表示未定位，也可抛 `InvalidOperationException` 表示调用时机错误，
但必须在实现自身的文档里写明所采用的一种；调用方不得依赖任何一侧的具体行为，
应先保证 `OpenAsync` 成功、并在释放后不再调用。

**Parameters**

- `position`: 目标位置。
- `ct`: 取消令牌。

**Returns**: `true` 表示定位请求被接受（非精确落点）；`false` 表示容器不可定位。

### Close

```csharp
void Close();
```

关闭解封装器。
