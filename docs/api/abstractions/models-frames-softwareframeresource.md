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

CPU 内存帧资源。实现 `IFrameResource`。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SoftwareFrameResource : IFrameResource
```

## Remarks

放在 Abstractions 的原因：Backends.FFmpeg 只引用 Abstractions，
FFmpegVideoDecoder 在软件解码路径中需要创建 SoftwareFrameResource。
如果放在 Renderers 模块，Backends 无法访问。

内存所有权：`Data` 表示资源拥有该内存。

- FFmpeg 软解拷贝路径: av_frame_get_buffer 分配 → 拷贝到 Memory&lt;byte&gt; → av_frame_free 释放原生帧
- ArrayPool 租借/归还内存，减少 GC 压力（60fps 每秒约 60 个帧）
- 零拷贝路径：`Data` 直接映射原生引用计数 buffer，
生命周期由中立 `IDisposable` 所有者控制（本层不依赖任何后端类型）
- Dispose 时: 归还 ArrayPool buffer 或释放零拷贝所有者（原生引用计数减一）

**AOT 兼容**：sealed 类，无反射，ArrayPool 为运行时内置 API。

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

CPU 内存数据（拥有所有权，底层由 ArrayPool、外部或原生引用计数 buffer 提供）。

### ColorInfo

```csharp
public VideoColorInfo? ColorInfo { get; set; }
```

帧的色彩空间描述（可空；由解码器透传，渲染端据此选 YUV→RGB 矩阵，null 时用默认）。

### Stride

```csharp
public int Stride { get; }
```

平面 0 的行字节数（stride）。`0` 表示未指定——数据为紧凑布局（宽度 × 每像素字节数），
兼容历史构造函数。零拷贝路径传入原生 buffer 的实际 stride，
可能因对齐填充大于紧凑行宽，渲染方须按行拷贝。

### _disposed

```csharp
public bool IsDisposed => _disposed;
```

Inherits documentation from its base contract.

### Constructor

```csharp
public SoftwareFrameResource(int width, int height, PixelFormat format, int dataLength);
```

从 `ArrayPool{T}` 租借内存创建实例，减少 GC 压力。

调用方通过 `Data`.`Span` 写入数据。
Dispose 时自动归还数组到 `ArrayPool{T}`。

**Parameters**

- `width`: 帧宽度（像素）。
- `height`: 帧高度（像素）。
- `format`: 像素格式。
- `dataLength`: 所需数据长度（字节）。实际租借的数组可能更长，但 &lt;see cref="Data"/&gt; 仅包含前 &lt;paramref name="dataLength"/&gt; 字节。

### Constructor

```csharp
public SoftwareFrameResource(int width, int height, PixelFormat format, Memory<byte> data);
```

使用外部提供的 `Memory{T}` 创建实例（不租借 ArrayPool）。
保留此构造函数兼容既有调用方（如 TestFrameFactory）。

**Parameters**

- `width`: 帧宽度（像素）。
- `height`: 帧高度（像素）。
- `format`: 像素格式。
- `data`: 外部提供的数据（调用方拥有所有权，Dispose 时不释放）。

### Constructor

```csharp
public SoftwareFrameResource(int width, int height, PixelFormat format,
    Memory<byte> data, int stride, IDisposable dataOwner);
```

零拷贝构造：`data` 直接映射原生引用计数 buffer，
生命周期由 `dataOwner` 控制。

**Parameters**

- `width`: 帧宽度（像素）。
- `height`: 帧高度（像素）。
- `format`: 像素格式。
- `data`: 映射原生内存的数据视图（不经拷贝）。
- `stride`: 平面 0 的行字节数（原生 buffer 实际 stride，可能含对齐填充）。
- `dataOwner`: 原生内存所有者（中立 &lt;see cref="IDisposable"/&gt;，后端传入引用计数句柄）。
Dispose 时释放该所有者，原生引用计数减一。所有者释放后不得再访问 &lt;see cref="Data"/&gt;。

### Dispose

```csharp
public void Dispose();
```

释放内存资源（归还 ArrayPool buffer 或释放零拷贝所有者）。
