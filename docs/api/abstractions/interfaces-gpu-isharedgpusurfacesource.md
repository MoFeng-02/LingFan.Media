---
title: "ISharedGpuSurfaceSourceFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/ISharedGpuSurfaceSource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ISharedGpuSurfaceSourceFactory", "HandleKind", "IsAvailable", "Create"]
lang: "en-US"
---
# ISharedGpuSurfaceSourceFactory

Source: [`ISharedGpuSurfaceSource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/ISharedGpuSurfaceSource.cs)

`ISharedGpuSurfaceSource` Factory (Neutral Pact). Multiple implementations registered by DI,
The consumer selects usable users based on the "host compositor-supported handle types."

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface ISharedGpuSurfaceSourceFactory
```

## Remarks

**Register and plug in**: Each GPU backend registers a factory; UI layer traversal
`IEnumerable<ISharedGpuSurfaceSourceFactory>`, select the first one
`IsAvailable` Factory where the handle type is supported by the host compositor.
Therefore, the UI layer does not contain any hard-coded branches that prioritize D3D11 / Vulkan second.

**Out-of-the-box principle**: `IsAvailable` should be considered lightweight (platform/DI availability),
**must not** touch native resources here; Actual device/texture creation is delayed to `Create`.

## Members

### HandleKind

```csharp
SharedGpuHandleKind HandleKind { get; }
```

Shared handle types produced by this factory (queries can be made without creating instances).

### IsAvailable

```csharp
bool IsAvailable { get; }
```

Is this adapter available in the current environment (platform matching, dependency ready). Lightweight determination, does not touch native resources.

### Create

```csharp
ISharedGpuSurfaceSource Create(SharedGpuAdapterIdentity? targetAdapter = null);
```

Create a shared surface source instance. The caller is responsible for releasing it.

**Parameters**

- `targetAdapter`: The physical GPU identity of the consumer (host compositor) (optional). Producers then select &lt;b&gt;the same physical equipment&lt;/b&gt; based on this,
If a match fails, &lt;see cref="NotSupportedException"/&gt; should be thrown to allow the caller to clean back to the next factory.
Empty means the consumer has not provided an identity, and producers choose according to their own preferences.

**Returns**: Shared surface source.
