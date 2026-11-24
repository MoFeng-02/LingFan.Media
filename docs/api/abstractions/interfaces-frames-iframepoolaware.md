---
title: "IFramePoolAware"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePoolAware.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFramePoolAware", "SetFramePool"]
lang: "en-US"
---
# IFramePoolAware

Source: [`IFramePoolAware.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePoolAware.cs)

Frame pool-aware interface. An optional implementation supports components of the frame object pool to receive pool references through this interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFramePoolAware<T> where T : class
```

## Remarks

Components such as decoders can optionally implement this interface. The caller (such as MediaPlayer) detects and injects it into the pool through pattern matching.

Existing interfaces (such as IMediaComponent/IVideoDecoder) are not modified; instead, pattern matching injections are used to maintain architecture compatibility.

**AOT Compatible**: interface + pattern matching (is operator), compile-time type determined, no reflection.

**Asynchronous Policy**: Synchronous configuration—pure memory settings referenced, no I/O.

## Members

### SetFramePool

```csharp
void SetFramePool(IFramePool<T>? pool);
```

Set the frame object pool. Passing null means pooling is disabled, and the component will create frame instances on its own.

**Parameters**

- `pool`: Frame object pool (can be null).
