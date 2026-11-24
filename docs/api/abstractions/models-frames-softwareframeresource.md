---
title: "SoftwareFrameResource"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/SoftwareFrameResource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SoftwareFrameResource", "Width", "Height", "Format", "Data", "ColorInfo", "Stride", "_disposed", "Dispose"]
lang: "en-US"
---
# SoftwareFrameResource

Source: [`SoftwareFrameResource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/SoftwareFrameResource.cs)

CPU memory frame resources. Implement `IFrameResource`.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SoftwareFrameResource : IFrameResource
```

## Remarks

Reason for placing it in Abstractions: Backends.FFmpeg only references Abstractions,
FFmpegVideoDecoder requires creating a SoftwareFrameResource in the software decoding path.
If placed in the Renderers module, Backends cannot be accessed.

Memory ownership: `Data` indicates the resource owns the memory.

- FFmpeg soft decopied copy path: av_frame_get_buffer allocate → copy to Memory&lt;byte&gt; → av_frame_free release native frames
- ArrayPool leasing/returning memory to reduce GC pressure (about 60fps at 60fps)
- zero-copy path: `Data` Direct mapping native reference count buffer,
Lifecycle is controlled by a neutral `IDisposable` owner (this layer does not depend on any backend type)
- Dispose: Return the ArrayPool buffer or release the owner of the zero-copy (native reference count minus one)

**AOT Compatible**: sealed class, non-reflective, ArrayPool is a built-in API at runtime.

## Members

### Width

```csharp
public int Width { get; }
```

Inherits documentation from its base contract.

### Height

```csharp
public int Height { get; }
```

Inherits documentation from its base contract.

### Format

```csharp
public PixelFormat Format { get; }
```

Inherits documentation from its base contract.

### Data

```csharp
public Memory<byte> Data { get; }
```

CPU memory data (ownership, provided at the base by ArrayPool, external or native reference count buffers).

### ColorInfo

```csharp
public VideoColorInfo? ColorInfo { get; set; }
```

Frame color space description (can be empty; Passed through by the decoder, the renderer selects YUV →RGB matrix, default when null).

### Stride

```csharp
public int Stride { get; }
```

Number of row bytes (stride) on plane 0. `0` means not specified—data is a compact layout (width × bytes per pixel),
Compatible with historical constructors. Passing the actual stride of the native buffer via a zero-copy path,
Because alignment padding may be greater than the compact line width, renderers must copy line by line.

### _disposed

```csharp
public bool IsDisposed => _disposed;
```

Inherits documentation from its base contract.

### Constructor

```csharp
public SoftwareFrameResource(int width, int height, PixelFormat format, int dataLength);
```

Create instances from `ArrayPool{T}` Leased memory to reduce GC pressure.

The caller writes data via `Data`.`Span`.
Automatically returns the array to `ArrayPool{T}` during Dispose.

**Parameters**

- `width`: Frame width (pixels).
- `height`: Frame height (pixels).
- `format`: Pixel format.
- `dataLength`: Required data length (in bytes). The actual leased array may be longer, but &lt;see cref="Data"/&gt; only contains the first &lt;paramref name="dataLength"/&gt; bytes.

### Constructor

```csharp
public SoftwareFrameResource(int width, int height, PixelFormat format, Memory<byte> data);
```

Create instances using externally provided `Memory{T}` (do not rent ArrayPool).
Keep this constructor compatible with existing callers (such as TestFrameFactory).

**Parameters**

- `width`: Frame width (pixels).
- `height`: Frame height (pixels).
- `format`: Pixel format.
- `data`: Externally provided data (owned by the caller, not released when Disposed).

### Constructor

```csharp
public SoftwareFrameResource(int width, int height, PixelFormat format,
    Memory<byte> data, int stride, IDisposable dataOwner);
```

zero-copy construction: `data` Direct mapping native reference count buffer,
The lifecycle is controlled by `dataOwner`.

**Parameters**

- `width`: Frame width (pixels).
- `height`: Frame height (pixels).
- `format`: Pixel format.
- `data`: Data view mapped to native memory (without copying).
- `stride`: Number of row bytes on plane 0 (native buffer actual stride, may include alignment padding).
- `dataOwner`: Native memory owner (neutral&lt;see cref="IDisposable"/&gt;, backend passes in reference count handle).
When Disposing, the owner is released, and the native reference count is minus one. Once the owner is released, access to &lt;see cref="Data"/&gt; is prohibited.

### Dispose

```csharp
public void Dispose();
```

Release memory resources (return the ArrayPool buffer or release the zero-copy owner).
