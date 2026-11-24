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

Media component lifecycle base interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaComponent : IDisposable, IAsyncDisposable
```

## Remarks

IDisposable + IAsyncDisposable dual inheritance: all components are unified and owned
void Dispose() (synchronous fast path) and ValueTask DisposeAsync() (asynchronous full path, recommended).

Interfaces that inherit this interface: IVideoDecoder, IAudioDecoder, ISubtitleDecoder,
IVideoRenderer、IAudioOutput、IMediaDemuxer。

Do not expose bool IsInitialized—state belongs to implementation.

## Members

### InitializeAsync

```csharp
Task InitializeAsync(CancellationToken ct = default);
```

Asynchronous initialization component.

**Parameters**

- `ct`: Cancel token.

**Returns**: Tasks that end upon completion of initialization.
