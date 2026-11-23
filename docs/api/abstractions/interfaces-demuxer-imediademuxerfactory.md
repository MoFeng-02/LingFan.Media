---
title: "IMediaDemuxerFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxerFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaDemuxerFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# IMediaDemuxerFactory

Source: [`IMediaDemuxerFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxerFactory.cs)

解封装器工厂接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaDemuxerFactory
```

## Remarks

Singleton 工厂，无状态。每次 Create() 返回新实例（每次播放新建，不共享）。

优先使用 `CreateAsync`（异步格式探测不阻塞线程，支持 CT）。

## Members

### Create

```csharp
IMediaDemuxer Create(IMediaStream stream);
```

根据媒体流创建对应的 IMediaDemuxer 实例。

### CreateAsync

```csharp
Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default);
```

异步根据媒体流创建对应的 IMediaDemuxer 实例。

**Parameters**

- `stream`: 媒体数据流。
- `ct`: 取消令牌。
