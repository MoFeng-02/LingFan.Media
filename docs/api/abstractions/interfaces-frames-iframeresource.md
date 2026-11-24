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

Frame resource interface. Unified resource management of CPU memory frames and GPU texture frames.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFrameResource : IDisposable
```

## Remarks

Known implementations:

- `SoftwareFrameResource` (CPU memory frames, purely managed implementation, located at this contract layer)
- GPU texture frames are implemented by various rendering/backend modules, such as VulkanImageResource and MetalTextureResource (corresponding to the rendering project)
- Platform-specific native frames (such as Android's AHardwareBuffer, Apple's CVPixelBuffer/IOSurface) are implemented by their respective platform modules and are not at this contract layer

The renderer side uses pattern matching to match types, ensuring AOT security.

## Members

### Width

```csharp
int Width { get; }
```

Frame width (pixels).

### Height

```csharp
int Height { get; }
```

Frame height (pixels).

### Format

```csharp
PixelFormat Format { get; }
```

Pixel format.
