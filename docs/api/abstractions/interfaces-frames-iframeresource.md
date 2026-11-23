---
title: "IFrameResource"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFrameResource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFrameResource", "Width", "Height", "Format"]
lang: "en-US"
---
# IFrameResource

Source: [`IFrameResource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFrameResource.cs)

帧资源接口。统一 CPU 内存帧和 GPU 纹理帧的资源管理。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFrameResource : IDisposable
```

## Remarks

已知实现：

- `SoftwareFrameResource`（CPU 内存帧，纯托管实现，位于本契约层）
- GPU 纹理帧由各渲染/后端模块实现，例如 VulkanImageResource、MetalTextureResource（分属对应渲染工程）
- 平台特定原生帧（如 Android 的 AHardwareBuffer、Apple 的 CVPixelBuffer/IOSurface）由对应平台模块实现，不在此契约层

Renderer 侧用 pattern matching 匹配类型，AOT 安全。

## Members

### Width

```csharp
int Width { get; }
```

帧宽度（像素）。

### Height

```csharp
int Height { get; }
```

帧高度（像素）。

### Format

```csharp
PixelFormat Format { get; }
```

像素格式。
