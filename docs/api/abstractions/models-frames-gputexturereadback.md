---
title: "GpuTextureReadback"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/GpuTextureReadback.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GpuTextureReadback", "Width", "Height", "Format", "Data", "Stride", "Dispose"]
lang: "en-US"
---
# GpuTextureReadback

Source: [`GpuTextureReadback.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/GpuTextureReadback.cs)

GPU 纹理 CPU 回读结果（中立、零外部引用）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class GpuTextureReadback : IDisposable
```

## Remarks

由 `ReadbackToCpu` 返回，承载紧凑 BGRA32 像素数据
（stride = 宽 × 4，无行内填充），供 SkiaVideoPresenter 直接写入 WriteableBitmap。

**生命周期**：回读数据为托管 `byte`[] 快照，与原生 GPU 纹理解耦；
调用方以 `using` 释放即可。设计上不持有原生句柄，避免与 GPU 帧资源双重释放——
帧所有权始终由 VideoPipeline 的 ReturnFrame 闭环。

**池化支持**：生产者可经池化构造传入 `ArrayPool{T}` 租借数组，
`Dispose` 时自动归还——消除每帧回读的大数组分配（1080p ≈ 8MB/帧 → LOH 压力）。
`ArrayPool{T}` 为 BCL 中立类型，契约层零外部引用约束不受影响。
消费方必须在 `using` 作用域内完成对 `Data` 的读取（归还后数据失效）。

## Members

### Width

```csharp
public int Width { get; }
```

帧宽度（像素）。

### Height

```csharp
public int Height { get; }
```

帧高度（像素）。

### Format

```csharp
public PixelFormat Format { get; }
```

回读像素格式（始终 BGRA32）。

### Data

```csharp
public Memory<byte> Data { get; }
```

BGRA32 像素数据（紧凑，stride = Width × 4）。

### Stride

```csharp
public int Stride { get; }
```

每行字节数（= Width × 4）。

### Constructor

```csharp
public GpuTextureReadback(int width, int height, PixelFormat format, byte[] data, int stride);
```

初始化回读结果。

**Parameters**

- `width`: 宽度。
- `height`: 高度。
- `format`: 回读格式（应为 BGRA32）。
- `data`: BGRA32 像素数据（管理型 byte[]）。
- `stride`: 每行字节数。

### Constructor

```csharp
public GpuTextureReadback(int width, int height, PixelFormat format, byte[] pooledData, int stride, int dataLength);
```

池化构造——包装 `ArrayPool{T}` 租借数组
（租借数组通常比所需更长，经 `dataLength` 截取有效区间），
`Dispose` 时自动归还池。

**Parameters**

- `width`: 宽度。
- `height`: 高度。
- `format`: 回读格式（应为 BGRA32）。
- `pooledData`: 自 &lt;see cref="System.Buffers.ArrayPool{T}"/&gt; 租借的数组（本实例接管归还责任）。
- `stride`: 每行字节数。
- `dataLength`: 有效数据长度（字节）。

### Dispose

```csharp
public void Dispose();
```

Inherits documentation from its base contract.
