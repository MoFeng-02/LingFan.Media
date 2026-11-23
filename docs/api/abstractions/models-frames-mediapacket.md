---
title: "MediaPacket"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/MediaPacket.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaPacket", "TrackIndex", "Data", "Timestamp", "Duration", "KeyFrame", "Width", "Height", "Stride", "SampleRate", "Channels", "Format", "_decodedFrameResource", "_disposed", "TakeDecodedFrameResource", "Dispose"]
lang: "en-US"
---
# MediaPacket

Source: [`MediaPacket.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/MediaPacket.cs)

解封装后的压缩数据包。实现 `IDisposable`。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaPacket : IDisposable
```

## Remarks

内存安全：

- 零拷贝路径：FFmpegDemuxer 通过 av_packet_clone 引用计数共享 FFmpeg 内部 buffer，
`Data` 直接映射原生内存，所有者以中立 `IDisposable` 传入
- 拷贝路径（无 dataOwner）：MediaPacket 独立拥有托管数据副本（兼容非引用计数来源）
- `Dispose` 释放所有者（原生引用计数减一）；释放后不得再访问 `Data`

## Members

### TrackIndex

```csharp
public int TrackIndex { get; }
```

所属轨道索引。

### Data

```csharp
public ReadOnlyMemory<byte> Data { get; }
```

压缩数据（只读视图，底层 buffer 由 packet 拥有）。

### Timestamp

```csharp
public TimeSpan Timestamp { get; }
```

时间戳（PTS）。

### Duration

```csharp
public TimeSpan Duration { get; }
```

数据包持续时间。

### KeyFrame

```csharp
public bool KeyFrame { get; }
```

是否关键帧。

### Width

```csharp
public int Width { get; }
```

解码帧宽度（仅直通 packet 有意义，压缩 packet 为 0）。

### Height

```csharp
public int Height { get; }
```

解码帧高度（压缩 packet 为 0）。

### Stride

```csharp
public int Stride { get; }
```

解码帧行跨度字节数（压缩 packet 为 0）。

### SampleRate

```csharp
public int SampleRate { get; }
```

音频采样率（压缩 packet 为 0）。

### Channels

```csharp
public int Channels { get; }
```

音频声道数（压缩 packet 为 0）。

### Format

```csharp
public SampleFormat Format { get; }
```

音频采样格式（压缩 packet 为 default）。

### _decodedFrameResource

```csharp
public IFrameResource? DecodedFrameResource => _decodedFrameResource;
```

已解码帧资源（可选）。仅"解封装 + 解码一体"的直通后端（如 MF SourceReader 自带硬解、VLC）会携带；
纯解封装后端（FFmpeg 压缩 packet）恒为 `null`。

**零拷贝语义**：该资源通常是 GPU 纹理（`IGpuTextureResource`），
由 demuxer 在读样阶段直接从原生 DXGI/硬件表面提取，无系统内存往返。

**所有权**：默认由本 packet 拥有，`Dispose` 时级联释放。
解码器若要把资源转交给 `VideoFrame`，必须调用 `TakeDecodedFrameResource`
显式移交所有权，避免 packet 释放时提前销毁仍在渲染管线中的纹理。

### null

```csharp
public bool HasDecodedFrameResource => _decodedFrameResource != null;
```

是否为"已解码直通" packet（携带 `DecodedFrameResource`）。
解码器据此走零拷贝直通分支，跳过自身解码。

### _disposed

```csharp
public bool IsDisposed => _disposed;
```

是否已释放。

### Constructor

```csharp
public MediaPacket(int trackIndex, ReadOnlyMemory<byte> data,
    TimeSpan timestamp, TimeSpan duration, bool keyFrame,
    IDisposable? dataOwner = null,
    int width = 0, int height = 0, int stride = 0,
    int sampleRate = 0, int channels = 0, SampleFormat format = default,
    IFrameResource? decodedFrameResource = null);
```

初始化 `MediaPacket` 的新实例。

**Parameters**

- `trackIndex`: 所属轨道索引。
- `data`: 压缩数据（托管副本或零拷贝原生视图）。
- `timestamp`: 时间戳（PTS）。
- `duration`: 数据包持续时间。
- `keyFrame`: 是否关键帧。
- `dataOwner`: 零拷贝所有者（可选）。非 null 时 &lt;paramref name="data"/&gt; 映射原生引用计数 buffer，
&lt;see cref="Dispose"/&gt; 释放该所有者使原生引用计数减一。
- `width`: 解码帧宽度（直通 packet 用，默认 0）。
- `height`: 解码帧高度（直通 packet 用，默认 0）。
- `stride`: 解码帧行跨度字节数（直通 packet 用，默认 0）。
- `sampleRate`: 音频采样率（直通 packet 用，默认 0）。
- `channels`: 音频声道数（直通 packet 用，默认 0）。
- `format`: 音频采样格式（直通 packet 用，默认 default）。
- `decodedFrameResource`: 已解码帧资源（可选）。解封装+解码一体后端（MF SourceReader 硬解 / VLC）传入 GPU 纹理或软件帧资源，
由本 packet 拥有，除非解码器调用 &lt;see cref="TakeDecodedFrameResource"/&gt; 移交所有权。

### TakeDecodedFrameResource

```csharp
public IFrameResource? TakeDecodedFrameResource()
    => Interlocked.Exchange(ref _decodedFrameResource, null);
```

取走 `DecodedFrameResource` 并移交所有权：调用后本 packet 不再持有该资源，
`Dispose` 也不会释放它，调用方负责最终释放（通常交由 `VideoFrame` 持有）。

线程安全：使用 `Interlocked` 交换，重复调用只有首次返回非 null。

**Returns**: 原资源；若本 packet 未携带解码帧资源或已被取走，返回 `null`。

### Dispose

```csharp
public void Dispose();
```

释放底层 buffer（零拷贝路径：原生引用计数减一）与未被取走的解码帧资源。
