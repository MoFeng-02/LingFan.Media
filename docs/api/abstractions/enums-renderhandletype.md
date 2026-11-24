---
title: "RenderHandleType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/RenderHandleType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["RenderHandleType", "None", "Pointer", "Texture", "Surface", "Context"]
lang: "en-US"
---
# RenderHandleType

Source: [`RenderHandleType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/RenderHandleType.cs)

Render handle type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum RenderHandleType : int
```

## Values

| Value | Description |
| --- | --- |
| `None` | No handle (e.g., Skia UI mode does not require native handles). |
| `Pointer` | Original pointer handles (such as HWND / NSView pointers). |
| `Texture` | GPU texture handles (such as D3D11 Texture2D / VkImage). |
| `Surface` | Surface handles (such as EGLSurface / CAMetalLayer). |
| `Context` | Context handles (such as GLContext / D3D11Device). |
