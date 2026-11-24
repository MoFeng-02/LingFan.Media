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

Pixel format.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum PixelFormat : int
```

## Values

| Value | Description |
| --- | --- |
| `YUV420P` | YUV 4:2:0 flat format. |
| `YUV422P` | YUV 4:2:2 flat format. |
| `YUV444P` | YUV 4:4:4 flat format. |
| `NV12` | NV12 semi-flat format (Y + UV interlacing). |
| `NV21` | NV21 half-plane format (Y + VU interlaced). |
| `P010` | P010 half-plane 10-bit format (NV12 layout: Y + UV interlacing, 10-bit per component stored in 16-bit bits). |
| `YUV420P10` | YUV420P10 Three-plane 10-bit format (Y + U + V each 10-bit stored in the 16-bit high-bit, ffmpeg software decoding the mainstream 10-bit frame layout). |
| `BGRA32` | BGRA 32-bit format (B8G8R8A8). |
| `RGBA32` | RGBA 32-bit format (R8G8B8A8). |
| `RGB24` | RGB 24-bit format (R8G8B8). |
