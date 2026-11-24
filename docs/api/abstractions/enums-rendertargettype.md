---
title: "RenderTargetType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/RenderTargetType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["RenderTargetType", "Window", "Texture", "Offscreen", "Custom"]
lang: "en-US"
---
# RenderTargetType

Source: [`RenderTargetType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/RenderTargetType.cs)

Render target type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum RenderTargetType : int
```

## Values

| Value | Description |
| --- | --- |
| `Window` | Window surface (SwapChain bound to window). |
| `Texture` | Texture targets (rendered to GPU textures, available to game engines). |
| `Offscreen` | Off-screen target (no window rendering). |
| `Custom` | Custom targets. |
