---
title: "RenderHandleType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/RenderHandleType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["RenderHandleType", "None", "Pointer", "Texture", "Surface", "Context"]
lang: "zh-CN"
---
# RenderHandleType

源码: [`RenderHandleType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/RenderHandleType.cs)

渲染句柄类型。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum RenderHandleType : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `None` | 无句柄（如 Skia UI 模式不需要原生句柄）。 |
| `Pointer` | 原始指针句柄（如 HWND / NSView 指针）。 |
| `Texture` | GPU 纹理句柄（如 D3D11 Texture2D / VkImage）。 |
| `Surface` | 表面句柄（如 EGLSurface / CAMetalLayer）。 |
| `Context` | 上下文句柄（如 GLContext / D3D11Device）。 |
