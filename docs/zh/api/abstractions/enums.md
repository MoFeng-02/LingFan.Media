# 枚举

全部 22 个跨层枚举都位于 `LingFan.Media.Abstractions`。

| 枚举 | 取值 |
|------|--------|
| `MediaState` | `Idle`, `Opening`, `Buffering`, `Playing`, `Paused`, `Stopped`, `Ended`, `Error` |
| `MediaSourceType` | `File`, `Network`, `Stream` |
| `ContainerFormat` | `MP4`, `MKV`, `AVI`, `TS`, `WebM`, `FLV`, `Unknown` |
| `TrackType` | `Video`, `Audio`, `Subtitle` |
| `VideoCodec` | `H264`, `H265`, `AV1`, `VP9`, `MPEG2`, `MPEG4`, `Unknown` |
| `AudioCodec` | `AAC`, `MP3`, `Opus`, `FLAC`, `Vorbis`, `PCM`, `AC3`, `Unknown` |
| `SubtitleCodec` | `SRT`, `ASS`, `PGS`, `VobSub`, `WebVTT`, `Unknown` |
| `PixelFormat` | `YUV420P`, `YUV422P`, `YUV444P`, `NV12`, `NV21`, `BGRA32`, `RGBA32`, `RGB24` |
| `SampleFormat` | `S16`, `S32`, `F32` |
| `BufferState` | `Empty`, `Buffering`, `Ready`, `Starved` |
| `MediaErrorCode` | `None`, `SourceNotFound`, `SourceOpenFailed`, `FormatNotSupported`, `CodecNotSupported`, `DecoderError`, `RendererError`, `AudioOutputError`, `NetworkError`, `BufferUnderrun`, `SeekFailed`, `OutOfMemory`, `GPUError`, `Unknown` |
| `ClockSyncSource` | `Audio`, `Video`, `System` |
| `GPUApiType` | `D3D11`, `Vulkan`, `Metal`, `OpenGL` |
| `AspectRatioMode` | `Fill`, `Uniform`, `UniformToFill` |
| `RenderTargetType` | `Window`, `Texture`, `Offscreen`, `Custom` |
| `RenderHandleType` | `None`, `Pointer`, `Texture`, `Surface`, `Context` |
| `VisualizerType` | `Spectrum`, `Waveform`, `Bars` |
| `SubtitlePosition` | `Bottom`, `Top`, `Center` |
| `SubtitleAlignment` | `Left`, `Center`, `Right` |
| `ProcessingMode` | `RealTime`, `Fastest` |
| `SharedGpuHandleKind` | `D3D11TextureGlobalSharedHandle`, `D3D11TextureNtHandle`, `VulkanOpaqueNtHandle`, `VulkanOpaquePosixFileDescriptor`, `IOSurfaceRef` |
| `SharedGpuSurfaceFormat` | `B8G8R8A8UNorm`, `R8G8B8A8UNorm` |

> `AudioCodec` / `SubtitleCodec` / `PixelFormat` / `MediaErrorCode` 成员的语义在源码 `Enums/` 文件夹中内联说明。`PGS` 与 `VobSub` 是 V1 中未实现的位图字幕编解码器。
