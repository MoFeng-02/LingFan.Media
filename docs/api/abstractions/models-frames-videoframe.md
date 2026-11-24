---
title: "VideoFrame"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/VideoFrame.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VideoFrame", "Width", "Height", "Format", "Resource", "Timestamp", "Duration", "KeyFrame", "ColorInfo", "RotationDegrees", "IsDisposed", "Reset", "Dispose"]
lang: "en-US"
---
# VideoFrame

Source: [`VideoFrame.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/VideoFrame.cs)

Video frame. Achieve `IDisposableFrame`, cascade release `Resource`.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VideoFrame : IDisposableFrame
```

## Remarks

Frame ownership transfer semantics: Decoder → FrameQueue → Renderer.

When Disposing, you must cascade and release IFrameResources (GPU textures/CPU memory) to prevent resource leaks.

The property uses the internal set,`Reset` method for the decoder to reuse frame instances (framepooling).

## Members

### Width

```csharp
public int Width { get; internal set; }
```

Frame width (pixels).

### Height

```csharp
public int Height { get; internal set; }
```

Frame height (pixels).

### Format

```csharp
public PixelFormat Format { get; internal set; }
```

Pixel format.

### Resource

```csharp
public IFrameResource? Resource { get; internal set; }
```

Frame resource (`SoftwareFrameResource`=CPU memory, or GPU texture=zero-copy handle).

Actual resources can be filled with null (unfilled shells in the pool), `Reset`.
The consumer accesses via pattern matching; nulls do not cause NRE.

### Timestamp

```csharp
public TimeSpan Timestamp { get; internal set; }
```

Display timestamps (PTS).

### Duration

```csharp
public TimeSpan Duration { get; internal set; }
```

Frame duration.

### KeyFrame

```csharp
public bool KeyFrame { get; internal set; }
```

Whether keyframes are available.

### ColorInfo

```csharp
public VideoColorInfo? ColorInfo { get; set; }
```

Frame color space description (can be empty; The rendering side selects YUV→RGB matrix accordingly, and uses the default matrix when null, see `VideoColorInfo`).

### RotationDegrees

```csharp
public int RotationDegrees { get; internal set; }
```

Display rotation (degrees, 0/90/180/270). Given by container metadata (such as MP4 tkhd rotation matrix),
The decoder output pixels themselves do not rotate—the rendering side must rotate at this angle for display (width and height reversal displayed at 90/270°).

### IsDisposed

```csharp
public bool IsDisposed { get; private set; }
```

Inherits documentation from its base contract.

### Constructor

```csharp
public VideoFrame(int width, int height, PixelFormat format, IFrameResource resource,
    TimeSpan timestamp, TimeSpan duration, bool keyFrame, VideoColorInfo? colorInfo = null,
    int rotationDegrees = 0);
```

Initialize a new instance of `VideoFrame`.

**Parameters**

- `width`: Frame width (pixels).
- `height`: Frame height (pixels).
- `format`: Pixel format.
- `resource`: Frame resources (CPU memory or GPU textures).
- `timestamp`: Display timestamps (PTS).
- `duration`: Frame duration.
- `keyFrame`: Whether keyframes are available.
- `colorInfo`: Color space description (can be left empty).
- `rotationDegrees`: Display rotation angle (0/90/180/270).

### Constructor

```csharp
public VideoFrame();
```

Non-parameter constructor (for FramePool factory to create empty shells).

For use only by frame object pools. The caller must populate the actual data using `Reset`.

### Reset

```csharp
public void Reset(int width, int height, PixelFormat format, IFrameResource? resource,
    TimeSpan timestamp, TimeSpan duration, bool keyFrame, VideoColorInfo? colorInfo = null,
    int rotationDegrees = 0);
```

Resets frame state for reuse by FramePool.
Release the old resource, set the new property value, and reset IsDisposed.

**Parameters**

- `width`: Frame width.
- `height`: Frame height.
- `format`: Pixel format.
- `resource`: Frame resource.
- `timestamp`: Display timestamps.
- `duration`: Frame duration.
- `keyFrame`: Whether keyframes are available.
- `colorInfo`: Color space description (can be left empty).
- `rotationDegrees`: Display rotation angle (0/90/180/270).

### Dispose

```csharp
public void Dispose();
```

Release frame resources, cascade release `Resource`.
