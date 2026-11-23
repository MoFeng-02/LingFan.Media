---
title: "IMediaDemuxerFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxerFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaDemuxerFactory", "Create", "CreateAsync"]
lang: "zh-CN"
---
# IMediaDemuxerFactory

源码: [`IMediaDemuxerFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Demuxer/IMediaDemuxerFactory.cs)

解封装器工厂接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaDemuxerFactory
```

## 备注

Singleton 工厂，无状态。每次 Create() 返回新实例（每次播放新建，不共享）。

优先使用 `CreateAsync`（异步格式探测不阻塞线程，支持 CT）。

## 成员

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

**参数**

- `stream`：媒体数据流。
- `ct`：取消令牌。
