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

视频帧。实现 `IDisposableFrame`，级联释放 `Resource`。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VideoFrame : IDisposableFrame
```

## Remarks

帧所有权转移语义：Decoder → FrameQueue → Renderer。

Dispose 时必须级联释放 IFrameResource（GPU 纹理/CPU 内存），防止资源泄漏。

属性使用 internal set，`Reset` 方法供解码器复用帧实例（帧池化）。

## Members

### Width

```csharp
public int Width { get; internal set; }
```

帧宽度（像素）。

### Height

```csharp
public int Height { get; internal set; }
```

帧高度（像素）。

### Format

```csharp
public PixelFormat Format { get; internal set; }
```

像素格式。

### Resource

```csharp
public IFrameResource? Resource { get; internal set; }
```

帧资源（`SoftwareFrameResource`=CPU 内存，或 GPU 纹理=零拷贝句柄）。

可为 null（池中未填充的空壳），`Reset` 填充实际资源。
消费方通过 pattern matching 访问，null 不会导致 NRE。

### Timestamp

```csharp
public TimeSpan Timestamp { get; internal set; }
```

显示时间戳（PTS）。

### Duration

```csharp
public TimeSpan Duration { get; internal set; }
```

帧持续时间。

### KeyFrame

```csharp
public bool KeyFrame { get; internal set; }
```

是否关键帧。

### ColorInfo

```csharp
public VideoColorInfo? ColorInfo { get; set; }
```

帧的色彩空间描述（可空；渲染端据此选 YUV→RGB 矩阵，null 时用默认矩阵，见 `VideoColorInfo`）。

### RotationDegrees

```csharp
public int RotationDegrees { get; internal set; }
```

显示旋转（度，0/90/180/270）。由容器元数据（如 MP4 tkhd 旋转矩阵）给出，
解码器输出像素本身不旋转——渲染端须按此角度旋转呈现（90/270° 时显示宽高互换）。

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

初始化 `VideoFrame` 的新实例。

**Parameters**

- `width`: 帧宽度（像素）。
- `height`: 帧高度（像素）。
- `format`: 像素格式。
- `resource`: 帧资源（CPU 内存或 GPU 纹理）。
- `timestamp`: 显示时间戳（PTS）。
- `duration`: 帧持续时间。
- `keyFrame`: 是否关键帧。
- `colorInfo`: 色彩空间描述（可空）。
- `rotationDegrees`: 显示旋转角度（0/90/180/270）。

### Constructor

```csharp
public VideoFrame();
```

无参构造函数（供 FramePool 工厂创建空壳）。

仅供帧对象池使用。调用方须通过 `Reset` 填充实际数据。

### Reset

```csharp
public void Reset(int width, int height, PixelFormat format, IFrameResource? resource,
    TimeSpan timestamp, TimeSpan duration, bool keyFrame, VideoColorInfo? colorInfo = null,
    int rotationDegrees = 0);
```

重置帧状态，供 FramePool 复用。
释放旧 Resource，设置新属性值，重置 IsDisposed。

**Parameters**

- `width`: 帧宽度。
- `height`: 帧高度。
- `format`: 像素格式。
- `resource`: 帧资源。
- `timestamp`: 显示时间戳。
- `duration`: 帧持续时间。
- `keyFrame`: 是否关键帧。
- `colorInfo`: 色彩空间描述（可空）。
- `rotationDegrees`: 显示旋转角度（0/90/180/270）。

### Dispose

```csharp
public void Dispose();
```

释放帧资源，级联释放 `Resource`。
