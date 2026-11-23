---
title: "VideoTrackInfo"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/VideoTrackInfo.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VideoTrackInfo", "Width", "Height", "FrameRate", "PixelFormat", "ColorSpace", "Sar", "Duration", "CodecConfiguration", "TimeBase"]
lang: "en-US"
---
# VideoTrackInfo

Source: [`VideoTrackInfo.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Tracks/VideoTrackInfo.cs)

视频轨道详情信息。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VideoTrackInfo
```

## Members

### Width

```csharp
public int Width { get; init; }
```

视频宽度（像素）。

### Height

```csharp
public int Height { get; init; }
```

视频高度（像素）。

### FrameRate

```csharp
public float FrameRate { get; init; }
```

帧率（FPS）。

### PixelFormat

```csharp
public PixelFormat PixelFormat { get; init; }
```

像素格式。

### ColorSpace

```csharp
public string? ColorSpace { get; init; }
```

色彩空间（可能为 null）。

### Sar

```csharp
public Rational? Sar { get; init; }
```

采样宽高比（可能为 null）。

### Duration

```csharp
public TimeSpan Duration { get; init; }
```

轨道时长。

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; set; }
```

编解码器私有配置（H264/H265 的 SPS+PPS / avcC / hvcC）。由解封装器从轨道媒体类型提取，
透传给解码器（MF 后端经 `MF_MT_MPEG_SEQUENCE_HEADER`）。契约层“只增不改”演进，零外部引用。
用可读写属性（非 init）：解封装器在轨道构建后于 ParseTracks 内回填。

### TimeBase

```csharp
public Rational TimeBase { get; set; }
```

流时间基（`AVStream.time_base`）。ffmpeg 解码帧的 pts/dts 以此为单位，须透传给解码器
写入 `AVCodecContext.pkt_timebase` 才能正确换算时间戳。解码后 `avFrame->time_base` /
`ctx->time_base` 常为 0，不能直接用于换算（否则帧时间戳全 0）。
