---
title: "IVideoRendererFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRendererFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoRendererFactory", "Create"]
lang: "en-US"
---
# IVideoRendererFactory

Source: [`IVideoRendererFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRendererFactory.cs)

Video renderer factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoRendererFactory
```

## Remarks

Singleton Factory. The GPU device context (IGpuDeviceContext) is owned by the factory

(Singleton shares GPU Devices), but SwapChain is created independently in Create().

## Members

### Create

```csharp
IVideoRenderer Create();
```

Create a new IVideoRenderer instance (sharing GPU Device, independent SwapChain).

**Returns**: Newly created video renderer instance (without a bound render target, must call `Attach`).
