---
title: "VaApiDmaBufDescriptor"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IVaApiExport.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VaApiDmaBufDescriptor", "Width", "Height", "DrmFourcc", "Modifier", "ObjectCount", "ObjectFds", "LayerCount", "PlaneObjectIndices", "PlaneOffsets", "PlanePitches"]
lang: "en-US"
---
# VaApiDmaBufDescriptor

Source: [`IVaApiExport.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IVaApiExport.cs)

dma_buf descriptor exported on the VAAPI surface (`VADRMPRIMESurfaceDescriptor` hosted carrier, zero external references).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VaApiDmaBufDescriptor
```

## Remarks

Filled by `TryExportSurfaceToDmaBuf`. Typical layout of composed NV12:
`ObjectCount=1` (single fd), `LayerCount=1`, single-layer `PlaneCount=2` (Y / UV),
Both planes share the same object index, each with offset/pitch, and share a DRM modifier.

Purely managed structure (no native boundary envelope), providing the decoder with flatten `GpuFrameImportSource` on the hosting side.

## Members

### Width

```csharp
public int Width;
```

Surface pixel width (pixels).

### Height

```csharp
public int Height;
```

Surface pixel height (pixels).

### DrmFourcc

```csharp
public uint DrmFourcc;
```

DRM fourcc (e.g., `0x3231564E` = 'NV12').

### Modifier

```csharp
public ulong Modifier;
```

DRM formatting modifiers for each object (single object when composed, shared across all planes).

### ObjectCount

```csharp
public int ObjectCount;
```

Number of objects (dma_buf fd). composed NV12 = 1；separate layers = 2。

### ObjectFds

```csharp
public int[]? ObjectFds;
```

dma_buf fd per object (length = `ObjectCount`).

### LayerCount

```csharp
public int LayerCount;
```

Number of layers (NV12 composed = 1, single layer contains 2 planes; separate = 2）。

### PlaneObjectIndices

```csharp
public uint[]? PlaneObjectIndices;
```

Index of objects belonging to each plane (length × 4 per layer; actual effective count is shown in each layer's PlaneCount).

### PlaneOffsets

```csharp
public uint[]? PlaneOffsets;
```

Byte offset per plane within the object (length with layers × 4).

### PlanePitches

```csharp
public uint[]? PlanePitches;
```

Row byte step length per plane (length with × 4 layers).
