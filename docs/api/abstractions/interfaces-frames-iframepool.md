---
title: "IFramePool"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePool.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFramePool", "Rent", "Return"]
lang: "en-US"
---
# IFramePool

Source: [`IFramePool.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePool.cs)

Frame object pool interface. Allows components such as decoders to reuse frame instances, reducing allocation and GC load.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFramePool<T> where T : class
```

## Remarks

**Threads**: `Rent` and `Return` must be callable from any thread—lease and return possible
This occurs in the decoding thread, pipeline thread, and UI thread, and must include thread safety (such as the concurrency stack) without requiring the caller to lock it.

**Ownership**: After `Rent`, frames belong to the caller; after `Return`, frames belong to the pool.
The caller must not continue to access the frame after returning it (the pool resets its contents, and frames holding GPU/buffer resources are released).

**Lifecycle**: This interface does not handle pool release—whether the pool is implemented `IDisposable`,
How the two methods behave after release is declared by implementation. Agreed Lower Limit: After release `Return` must be **released directly**
Incoming frames rather than inbound frames; `Rent` Allow the return of new instances (no exception throwing), causing delayed returns to degenerate into releases rather than leaks.

## Members

### Rent

```csharp
T Rent();
```

Rent a frame instance.

**Returns**: Reusable frame instances (new instances when the pool is empty); Ownership belongs to the caller, and after use, `Return` is required.

### Return

```csharp
void Return(T frame);
```

Return the frame instance to the pool.

**Parameters**

- `frame`: Previously obtained from this pool &lt;see cref="Rent"/&gt; frame; After returning, no further visits are permitted.
