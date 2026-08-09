# Enumerations

All 22 cross-layer enums live in `LingFan.Media.Abstractions`.

| Enum | Values |
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

> `AudioCodec` / `SubtitleCodec` / `PixelFormat` / `MediaErrorCode` member semantics are documented inline in the source `Enums/` folder. `PGS` and `VobSub` are bitmap subtitle codecs not implemented in V1.
