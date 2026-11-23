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

渲染目标接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IRenderTarget
```

## Remarks

不叫 IRenderSurface——渲染目标不一定是窗口 Surface。

游戏引擎可渲染到 Texture → Material → Mesh。

NativeHandle 用 object 而非 IntPtr——AOT 安全（非热路径，显式 cast）。

## Members

### Type

```csharp
RenderTargetType Type { get; }
```

渲染目标类型（Window / Texture / Offscreen / Custom）。

### HandleType

```csharp
RenderHandleType HandleType { get; }
```

渲染句柄类型（None / Pointer / Texture / Surface / Context）。

### NativeHandle

```csharp
object NativeHandle { get; }
```

原生句柄（运行时显式 cast，不用反射）。

### Width

```csharp
int Width { get; }
```

宽度。

### Height

```csharp
int Height { get; }
```

高度。

### Scale

```csharp
float Scale { get; }
```

DPI 缩放比。
