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

VAAPI 表面导出的 dma_buf 描述符（`VADRMPRIMESurfaceDescriptor` 托管载体，零外部引用）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VaApiDmaBufDescriptor
```

## Remarks

由 `TryExportSurfaceToDmaBuf` 填充。composed NV12 典型布局：
`ObjectCount=1`（单 fd）、`LayerCount=1`、单层 `PlaneCount=2`（Y / UV），
两平面共享同一对象索引、各自带 offset / pitch、共享一个 DRM 修饰符。

纯托管结构（不跨原生边界封送），供解码器在托管侧 flatten 为 `GpuFrameImportSource` 的逐平面视图。

## Members

### Width

```csharp
public int Width;
```

表面像素宽（像素）。

### Height

```csharp
public int Height;
```

表面像素高（像素）。

### DrmFourcc

```csharp
public uint DrmFourcc;
```

DRM fourcc（如 `0x3231564E` = 'NV12'）。

### Modifier

```csharp
public ulong Modifier;
```

每对象 DRM 格式修饰符（composed 时单对象，所有平面共享）。

### ObjectCount

```csharp
public int ObjectCount;
```

对象数（dma_buf fd 数）。composed NV12 = 1；separate layers = 2。

### ObjectFds

```csharp
public int[]? ObjectFds;
```

每对象的 dma_buf fd（长度 = `ObjectCount`）。

### LayerCount

```csharp
public int LayerCount;
```

层数（NV12 composed = 1 单层含 2 平面；separate = 2）。

### PlaneObjectIndices

```csharp
public uint[]? PlaneObjectIndices;
```

每平面所属对象索引（长度随层数 × 4，实际有效数见各层 PlaneCount）。

### PlaneOffsets

```csharp
public uint[]? PlaneOffsets;
```

每平面在对象内的字节偏移（长度随层数 × 4）。

### PlanePitches

```csharp
public uint[]? PlanePitches;
```

每平面的行字节步幅（长度随层数 × 4）。
