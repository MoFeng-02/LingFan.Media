---
title: "IMediaComponent"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IMediaComponent.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaComponent", "InitializeAsync"]
lang: "en-US"
---
# IMediaComponent

Source: [`IMediaComponent.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IMediaComponent.cs)

媒体组件生命周期基接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaComponent : IDisposable, IAsyncDisposable
```

## Remarks

IDisposable + IAsyncDisposable 双继承：所有组件统一拥有
void Dispose()（同步快速路径）和 ValueTask DisposeAsync()（异步完整路径，推荐）。

继承此接口的接口：IVideoDecoder、IAudioDecoder、ISubtitleDecoder、
IVideoRenderer、IAudioOutput、IMediaDemuxer。

不暴露 bool IsInitialized——状态属于实现。

## Members

### InitializeAsync

```csharp
Task InitializeAsync(CancellationToken ct = default);
```

异步初始化组件。

**Parameters**

- `ct`: 取消令牌。

**Returns**: 初始化完成时结束的任务。
