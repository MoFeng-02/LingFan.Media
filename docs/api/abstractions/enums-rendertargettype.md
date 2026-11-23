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

渲染目标类型。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum RenderTargetType : int
```

## Values

| Value | Description |
| --- | --- |
| `Window` | 窗口表面（SwapChain 绑定到窗口）。 |
| `Texture` | 纹理目标（渲染到 GPU 纹理，游戏引擎可用）。 |
| `Offscreen` | 离屏目标（无窗口渲染）。 |
| `Custom` | 自定义目标。 |
