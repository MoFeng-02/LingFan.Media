---
title: "MediaErrorCode"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaErrorCode.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaErrorCode", "None", "SourceNotFound", "SourceOpenFailed", "FormatNotSupported", "CodecNotSupported", "DecoderError", "RendererError", "AudioOutputError", "NetworkError", "BufferUnderrun", "SeekFailed", "OutOfMemory", "GPUError", "Unknown"]
lang: "zh-CN"
---
# MediaErrorCode

源码: [`MediaErrorCode.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaErrorCode.cs)

媒体错误码。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum MediaErrorCode : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `None` | 无错误。 |
| `SourceNotFound` | 媒体源未找到。 |
| `SourceOpenFailed` | 媒体源打开失败。 |
| `FormatNotSupported` | 容器格式不支持。 |
| `CodecNotSupported` | 编解码器不支持。 |
| `DecoderError` | 解码器错误。 |
| `RendererError` | 渲染器错误。 |
| `AudioOutputError` | 音频输出错误。 |
| `NetworkError` | 网络错误。 |
| `BufferUnderrun` | 缓冲不足。 |
| `SeekFailed` | 定位失败。 |
| `OutOfMemory` | 内存不足。 |
| `GPUError` | GPU 错误。 |
| `Unknown` | 未知错误。 |
