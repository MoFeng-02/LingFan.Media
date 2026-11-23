---
title: "PixelFormat"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/PixelFormat.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["PixelFormat", "YUV420P", "YUV422P", "YUV444P", "NV12", "NV21", "P010", "YUV420P10", "BGRA32", "RGBA32", "RGB24"]
lang: "en-US"
---
# PixelFormat

Source: [`PixelFormat.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/PixelFormat.cs)

像素格式。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum PixelFormat : int
```

## Values

| Value | Description |
| --- | --- |
| `YUV420P` | YUV 4:2:0 平面格式。 |
| `YUV422P` | YUV 4:2:2 平面格式。 |
| `YUV444P` | YUV 4:4:4 平面格式。 |
| `NV12` | NV12 半平面格式（Y + UV 交错）。 |
| `NV21` | NV21 半平面格式（Y + VU 交错）。 |
| `P010` | P010 半平面 10-bit 格式（NV12 布局：Y + UV 交错，每分量 10bit 存于 16-bit 高位）。 |
| `YUV420P10` | YUV420P10 三平面 10-bit 格式（Y + U + V 各自 10bit 存于 16-bit 高位，ffmpeg 软解 10-bit 的主流出帧布局）。 |
| `BGRA32` | BGRA 32位格式（B8G8R8A8）。 |
| `RGBA32` | RGBA 32位格式（R8G8B8A8）。 |
| `RGB24` | RGB 24位格式（R8G8B8）。 |
