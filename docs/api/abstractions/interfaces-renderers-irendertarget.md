---
title: "IRenderTarget"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IRenderTarget.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IRenderTarget", "Type", "HandleType", "NativeHandle", "Width", "Height", "Scale"]
lang: "en-US"
---
# IRenderTarget

Source: [`IRenderTarget.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IRenderTarget.cs)

Render target interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IRenderTarget
```

## Remarks

Not called IRenderSurface—the rendering target is not necessarily a window Surface.

The game engine can render to Texture→ Material, → Mesh.

NativeHandle uses objects instead of IntPtr—AOT safe (non-hot path, explicit cast).

## Members

### Type

```csharp
RenderTargetType Type { get; }
```

Render target types (Window / Texture / Offscreen / Custom).

### HandleType

```csharp
RenderHandleType HandleType { get; }
```

Render handle types (None / Pointer / Texture / Surface / Context).

### NativeHandle

```csharp
object NativeHandle { get; }
```

Native handle (runtime explicit cast, no reflection needed).

### Width

```csharp
int Width { get; }
```

Width.

### Height

```csharp
int Height { get; }
```

Height.

### Scale

```csharp
float Scale { get; }
```

DPI scaling ratio.
