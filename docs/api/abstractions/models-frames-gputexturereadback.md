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

GPU texture CPU readback results (neutral, zero external references).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class GpuTextureReadback : IDisposable
```

## Remarks

Returned by `ReadbackToCpu`, carrying compact BGRA32 pixel data
(stride = width × 4, no inline padding), for SkiaVideoPresenter to write directly to the WriteableBitmap.

**Lifecycle**: Readback data is hosted as `byte`[] snapshot, decoupled from native GPU texture;
The caller can be released with `using`. The design does not hold native handles, avoiding dual release of GPU frame resources—
Frame ownership is always closed in the ReturnFrame loop of VideoPipeline.

**Pooling Support**: Producers can pass in `ArrayPool{T}` leased arrays via pooling constructions,
`Dispose` Automatic return — eliminates large array allocations per frame readback (1080p ≈ 8MB/frame→ LOH pressure).
`ArrayPool{T}` is a BCL-neutral type, and the contract layer has zero external reference constraints that are unaffected.
The consumer must complete the read of `Data` within the scope of `using` (data becomes invalid after return).

## Members

### Width

```csharp
public int Width { get; }
```

Frame width (pixels).

### Height

```csharp
public int Height { get; }
```

Frame height (pixels).

### Format

```csharp
public PixelFormat Format { get; }
```

Readback pixel format (always BGRA32).

### Data

```csharp
public Memory<byte> Data { get; }
```

BGRA32 pixel data (compact, stride = width × 4).

### Stride

```csharp
public int Stride { get; }
```

Bytes per line (= Width × 4).

### Constructor

```csharp
public GpuTextureReadback(int width, int height, PixelFormat format, byte[] data, int stride);
```

Initialize the readback result.

**Parameters**

- `width`: Width.
- `height`: Height.
- `format`: Read back format (should be BGRA32).
- `data`: BGRA32 pixel data (managed byte[]).
- `stride`: Bytes per line.

### Constructor

```csharp
public GpuTextureReadback(int width, int height, PixelFormat format, byte[] pooledData, int stride, int dataLength);
```

Pooling Construct — wrapping `ArrayPool{T}` rented arrays
(The rental array is usually longer than needed, and the valid interval is truncated by `dataLength`),
`Dispose` automatically returns to the pool.

**Parameters**

- `width`: Width.
- `height`: Height.
- `format`: Read back format (should be BGRA32).
- `pooledData`: Leased arrays from &lt;see cref="System.Buffers.ArrayPool{T}"/&gt; (this instance assumes responsibility for return).
- `stride`: Bytes per line.
- `dataLength`: Effective data length (bytes).

### Dispose

```csharp
public void Dispose();
```

Inherits documentation from its base contract.
