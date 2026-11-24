---
title: "MediaErrorCode"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaErrorCode.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaErrorCode", "None", "SourceNotFound", "SourceOpenFailed", "FormatNotSupported", "CodecNotSupported", "DecoderError", "RendererError", "AudioOutputError", "NetworkError", "BufferUnderrun", "SeekFailed", "OutOfMemory", "GPUError", "Unknown"]
lang: "en-US"
---
# MediaErrorCode

Source: [`MediaErrorCode.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaErrorCode.cs)

Media error code.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum MediaErrorCode : int
```

## Values

| Value | Description |
| --- | --- |
| `None` | No errors. |
| `SourceNotFound` | Media source not found. |
| `SourceOpenFailed` | Media source failed to open. |
| `FormatNotSupported` | Container format is not supported. |
| `CodecNotSupported` | Codec not supported. |
| `DecoderError` | Decoder error. |
| `RendererError` | Renderer error. |
| `AudioOutputError` | Audio output error. |
| `NetworkError` | Network error. |
| `BufferUnderrun` | Insufficient buffering. |
| `SeekFailed` | Positioning failed. |
| `OutOfMemory` | Insufficient memory. |
| `GPUError` | GPU error. |
| `Unknown` | Unknown error. |
