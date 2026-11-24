---
title: "IRendererHealth"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IRendererHealth.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IRendererHealth", "Unhealthy"]
lang: "en-US"
---
# IRendererHealth

Source: [`IRendererHealth.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IRendererHealth.cs)

Renderer runtime health notification contract.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IRendererHealth
```

## Remarks

**Purpose**: The renderer may succeed at `Attach` but cannot output during runtime
(For example, Composition compositor fails to import cross-device textures). If the game remains silent and blank without reverting, it violates the rule of "not allowing a renderer to become unplayable."
Fairness requirements. The renderer implementing this interface triggers `Unhealthy` when it repeatedly fails, and the host uses this to trigger the renderer
Block and rebuild the fallback chain (such as Composition → Skia) to ensure there is always an available path to the drawing.

**Decoupling**: Defined in Abstractions, the UI layer (VideoView) subscribes only by contract and does not reference specific renderer types.

**Trigger Diameter**: This event is a **single ** signal—the same instance can trigger at most once, and must be prevented by using an atomic gate
Pipeline threads and UI threads trigger concurrently and repeatedly. The threshold for "consecutive failures" is self-defined by implementation,
The contract only requires the criterion to be **consecutive** failures rather than cumulative failures, and threshold ≥ 1: a single burst failure should not directly block the renderer.

**No Recovery Channel**: This contract does not provide "recovery of health" notifications. After receiving it, the host should include the instance along with its factory type
Move out of the candidate chain and roll back to the next renderer; The same instance will not be restored to availability through this contract, nor can it be re-enabled.

**AOT Compatible**: Declare only events, no reflections.

## Members

### Unhealthy

```csharp
event Action? Unhealthy;
```

Triggered once when the renderer runtime is unhealthy (continuous failure to render).
Triggered by the renderer in the pipeline thread; The host must switch back to UI thread processing (rebuild the fallback chain) to avoid cross-thread control operations.
