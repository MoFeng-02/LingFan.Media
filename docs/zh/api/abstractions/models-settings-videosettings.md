---
title: "VideoSettings"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/VideoSettings.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["VideoSettings", "EnableHardwareAcceleration", "PreferredCodec", "MaxWidth", "MaxHeight", "Width", "Height", "OutputPixelFormat", "CodecConfiguration", "TimeBase"]
lang: "zh-CN"
---
# VideoSettings

源码: [`VideoSettings.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/VideoSettings.cs)

视频解码与渲染设置。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class VideoSettings
```

## 成员

### EnableHardwareAcceleration

```csharp
public bool EnableHardwareAcceleration { get; init; } = true;
```

是否启用硬件加速解码。

### PreferredCodec

```csharp
public VideoCodec? PreferredCodec { get; init; }
```

首选编解码器（null 表示自动选择）。

### MaxWidth

```csharp
public int? MaxWidth { get; init; }
```

最大解码宽度（null 表示不限制）。

### MaxHeight

```csharp
public int? MaxHeight { get; init; }
```

最大解码高度（null 表示不限制）。

### Width

```csharp
public int? Width { get; init; }
```

源视频宽度（像素，来自轨道信息；null 表示未知）。
供需要显式宽高的解码后端使用（如 Android MediaCodec 的 `configure` 部分设备/解码器
要求显式 `width`/`height`，仅 csd-0 推导不足会返回 EINVAL）。

### Height

```csharp
public int? Height { get; init; }
```

源视频高度（像素，来自轨道信息；null 表示未知）。
见 `Width`。

### OutputPixelFormat

```csharp
public PixelFormat? OutputPixelFormat { get; init; }
```

输出像素格式（null 表示使用源格式）。

### CodecConfiguration

```csharp
public ReadOnlyMemory<byte> CodecConfiguration { get; init; }
```

编解码器私有配置（如 H264/H265 的 SPS+PPS / avcC / hvcC），由解封装器在轨道信息中提供，
透传给解码器设置输入媒体类型（MF 后端经 `MF_MT_MPEG_SEQUENCE_HEADER`，FFmpeg 等后端通常忽略）。
零外部引用（BCL `ReadOnlyMemory{T}`），符合契约层“只增不改”演进准则。

### TimeBase

```csharp
public Rational TimeBase { get; init; }
```

流时间基（`AVStream.time_base`）。透传自轨道信息，供 FFmpeg 等后端写入
`AVCodecContext.pkt_timebase`，使解码帧 pts/dts 换算为正确秒数。默认 `default`（无效）。
