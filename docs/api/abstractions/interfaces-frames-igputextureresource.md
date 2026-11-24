---
title: "IGpuTextureResource"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuTextureResource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuTextureResource", "NativeTextureHandle", "SubresourceIndex", "ReadbackToCpu"]
lang: "en-US"
---
# IGpuTextureResource

Source: [`IGpuTextureResource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuTextureResource.cs)

Neutral GPU texture frame resource contract (shared across layers, zero external references).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuTextureResource : IFrameResource
```

## Remarks

Implemented by specific renderers/backends (e.g., `LingFan.Media.Renderers.Vulkan.VulkanImageResource`),
allowing layers like Avalonia / Outputs to consume GPU textures in a platform-independent manner (for example, CPU readback and sending a Skia WriteableBitmap fallback path),
Without referencing specific renderer modules, strictly adhering to dependency inversion. Along with `IGpuDeviceContext`, it is an Abstractions neutral bridge.

**Asynchronous Strategy**: `ReadbackToCpu` Synchronizes COM/native calls—D3D11/GL texture readback
No real I/O await. Implements synchronization without async (padding means pseudo-asynchronous).

## Members

### NativeTextureHandle

```csharp
IntPtr NativeTextureHandle { get; }
```

Native GPU texture handles (such as the COM pointer for ID3D11Texture2D*).
Allows renderers to copy directly to the GPU (zero-copy path), no CPU readback required.

The caller is responsible for ensuring that resources are not released during use (such as renderer locks or frame reference counts).
Handle ownership is managed by `IFrameResource` implemented `Dispose`.

### SubresourceIndex

```csharp
int SubresourceIndex { get; }
```

Sub-resource index (texture array slice index, 0 if not array).
When hard-decoding texture arrays with DXVA, this index must be used to copy the correct slices via CopySubresourceRegion.

### ReadbackToCpu

```csharp
GpuTextureReadback ReadbackToCpu();
```

GPU textures are read back to the CPU, returning compact BGRA32 pixel data (for Avalonia Skia to write directly to WriteableBitmap).
Implementation responsible for creating staging textures, copying, Map reading, and converting them to BGRA32 in source format.

**Returns**: CPU readback results (neutral `GpuTextureReadback`, dispose by the caller).
