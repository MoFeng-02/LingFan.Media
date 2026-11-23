---
title: "IMediaStreamFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStreamFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaStreamFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# IMediaStreamFactory

Source: [`IMediaStreamFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStreamFactory.cs)

媒体流工厂接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaStreamFactory
```

## Remarks

Singleton 工厂。每次 Create() 返回新实例。

持有 IHttpClientFactory 引用（用于网络流连接池管理，非纯无状态）。

优先使用 `CreateAsync`（网络流场景不阻塞线程，支持 CT）。

## Members

### Create

```csharp
IMediaStream Create(IMediaSource source);
```

根据媒体源类型创建对应的 IMediaStream 实例。

**Parameters**

- `source`: 媒体源。

**Returns**: 新创建的媒体流实例（网络流须先 `ConnectAsync` 再同步读取）。

### CreateAsync

```csharp
Task<IMediaStream> CreateAsync(IMediaSource source, CancellationToken ct = default);
```

异步根据媒体源类型创建对应的 IMediaStream 实例。

**Parameters**

- `source`: 媒体源。
- `ct`: 取消令牌。

**Returns**: 新创建的媒体流实例。
