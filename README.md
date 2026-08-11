<p align="center">
  <img src="logo/LingFan.png" alt="LingFan Media" width="220" />
</p>

# LingFan.Media (灵泛)

> 中文文档：[README.zh.md](README.zh.md)

**LingFan.Media (灵泛)** is a cross-platform media infrastructure for the .NET platform. It provides a modular, DI-friendly, and AOT-ready abstraction layer that decouples core playback logic from the concrete engines (decoders, demuxers, renderers, audio outputs) so they can be swapped per platform or per deployment.

> Status: The library is actively developed on **.NET 10**. The primary validated target today is **Windows**; Linux support is implemented through the FFmpeg and LibVLC backends, and other platforms are on the roadmap (see [Platform & backend status](#platform--backend-status)). It is not yet a feature-complete, every-platform media framework — the design is built to get there without breaking the public surface. **Only local-file playback has been validated end-to-end so far; network-source and streaming paths are implemented but not yet runtime-validated.**

## Why another media layer

- **Modular, not monolithic.** Backends, renderers, and audio outputs are independent components registered through dependency injection. Adding or replacing one does not touch the core.
- **DI-driven composition.** You assemble exactly the pipeline you need (`AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`) instead of pulling in a fixed engine.
- **AOT-ready.** The codebase targets `net10.0` with `IsAotCompatible=true`, avoids reflection-based activation and `ComImport`, and uses source-generated P/Invoke (`[LibraryImport]`) for native interop, so it can be published as a NativeAOT binary.
- **Headless-first.** The same playback pipeline drives both server-side / off-screen processing and on-screen rendering. Video frames are delivered through a single frame channel; headless consumers subscribe to frames, while UI renderers present them to the platform compositor.
- **Contract layer stays clean.** Higher layers depend only on the `Abstractions` contracts; concrete backends and renderers are injected, preserving dependency inversion.

## Platform & backend status

| Platform | Status | Available backends |
| --- | --- | --- |
| **Windows** | Supported (primary, validated) | MediaFoundation (native, hardware-decode capable), FFmpeg, LibVLC |
| **Linux** | Implemented via FFmpeg + LibVLC with Vulkan / OpenGL renderers; validation ongoing | FFmpeg, LibVLC |
| **macOS / iOS / Android** | Roadmap — the architecture accommodates them, but they are not yet validated | — |

Backends share one pluggable model, so the same `IMediaPlayer` surface works regardless of which engine is selected. The backend selection is resolved at runtime based on what you registered.

> Note: WebRTC / GStreamer are explicitly out of the current scope.

## Status & maturity

The library is further along in some areas than others. The table below marks each capability as validated end-to-end, implemented but not yet runtime-validated, under active validation, on the roadmap, or explicitly out of scope.

**Maturity journey:** V1 Windows (validated) → multi-backend (validated) → Linux validation (in progress) → macOS / iOS / Android (roadmap). WebRTC and GStreamer are out of scope.

| Capability | Status |
| --- | --- |
| Local-file playback (Windows) | **Validated** |
| D3D11 renderer (Windows) | **Validated** |
| WASAPI audio output (Windows) | **Validated** |
| Headless frame delivery (frame channel) | **Validated** |
| MediaFoundation backend | **Validated** |
| FFmpeg backend | **Validated** |
| LibVLC backend | **Validated** |
| Network sources (`NetworkMediaSource` + SSRF) | Implemented, not yet validated |
| Streaming playback | Implemented, not yet validated |
| Linux (FFmpeg + LibVLC + Vulkan / OpenGL) | **Validation in progress** |
| Vulkan / OpenGL renderers | **Validation in progress** |
| macOS / iOS / Android | Roadmap |
| WebRTC / GStreamer | Out of scope |

> The validated Windows path exercises the core abstraction, rendering, audio output, and headless frame delivery on a local file. Network and streaming paths are implemented (including DNS-pinning SSRF protection) but have not yet been exercised end-to-end — treat them as experimental until validated at runtime.

> **Hardware decode:** MediaFoundation's decoder returns frames through CPU memory (hybrid decode) on Windows. This is a characteristic of the platform's MFT pipeline, not a defect in LingFan.Media. The FFmpeg and LibVLC backends provide full GPU-resident hardware decode. All three backends play local files correctly on Windows.

## Installation

LingFan.Media is built from this repository. Production libraries are packed via `dotnet pack` (Apache-2.0, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party LGPL obligations such as FFmpeg and LibVLC).

Reference the projects you need, or consume the produced NuGet packages in your application.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using LingFan.Media.Abstractions;        // IMediaPlayer, IMediaPlayerFactory, IMediaSource
using LingFan.Media.Extensions;          // AddLingFanMedia
using LingFan.Media.Backends.FFmpeg;     // AddFFmpeg
using LingFan.Media.Renderers.D3D11;     // AddD3D11Renderer
using LingFan.Media.Outputs.WASAPI;      // AddWasapiOutput
using LingFan.Media.Sources;             // FileMediaSource

// 1. Compose the pipeline (backends + renderer + audio output).
var services = new ServiceCollection();
services.AddLingFanMedia()
        .AddFFmpeg()
        .AddD3D11Renderer()
        .AddWasapiOutput();
var provider = services.BuildServiceProvider();

// 2. Resolve a player. The fallback factory auto-selects a registered backend.
var factory = provider.GetRequiredService<IMediaPlayerFactory>();
using var player = factory.Create();

// 3. Headless: subscribe to frames. (For on-screen rendering, attach a UI presenter instead.)
player.VideoFrameAvailable += (VideoFrame frame) =>
{
    // Read-only borrow the frame inside the callback.
    // Do NOT Dispose it and do not retain the reference across threads.
};

// 4. Open and play.
await player.OpenAsync(new FileMediaSource(@"C:\videos\clip.mp4"));
await player.PlayAsync();

// Control during playback:
await player.PauseAsync();
await player.SeekAsync(TimeSpan.FromSeconds(30));

// When finished:
await player.StopAsync();
await player.DisposeAsync();
```

### Choosing a backend explicitly

`AddMediaFoundation()` (Windows), `AddFFmpeg()`, and `AddVLCNative()` register their factories into the DI container. When multiple are registered, the runtime selects one in registration order and falls back if a backend cannot handle the source. To force a specific backend, use the `IMediaPlayerFactory.Create(...)` overload that accepts an explicit backend group.

### Network sources (experimental, not yet validated)

`NetworkMediaSource` is implemented with DNS-pinning–based SSRF protection: private, loopback, link-local, and reserved/CGNAT addresses are rejected at construction, and the resolved IP is pinned for the actual connection so a redirected URL cannot reach an internal address. **However, network and streaming playback have not yet been exercised end-to-end** — only local-file playback has been tested so far. Treat network sources as experimental until they are validated at runtime.

## Architecture in brief

```
┌─────────────────────────────────────────────┐
│  Abstractions (contracts: IMediaPlayer,      │
│  IMediaSource, IFrameChannel, frame models) │  ← zero external references
├─────────────────────────────────────────────┤
│  Core / Playback  (orchestration, clock,     │
│  frame routing, session lifecycle)          │
├──────────────┬───────────────┬───────────────┤
│  Backends    │  Renderers    │  Audio Outputs │  ← pluggable, DI-registered
│ (MF/FFmpeg/  │ (D3D11/      │  (WASAPI /     │
│  VLC)        │  Vulkan/GL)  │   headless)    │
└──────────────┴───────────────┴───────────────┘
```

- **Frame routing** is unified: every decoded video frame flows through one frame channel. A headless consumer borrows frames via the `VideoFrameAvailable` event; a UI renderer presents them to the platform compositor. There is no second, divergent delivery path.
- **Session isolation**: each `IMediaPlayer` owns an independent session (clock, buffers, pipelines). Infrastructure factories are singletons; sessions are created per player.
- **Processing modes**: `Mode = ProcessingMode.Fastest` disables A/V synchronization and real-time throttling for batch / offline scenarios (transcoding, ML inference); `RealTime` is the default for normal playback.

## Usage notes & caveats

- **Async / sync split.** I/O-bound operations expose async signatures (`OpenAsync`, `StopAsync`, `SeekAsync`) and accept a `CancellationToken`. Pure in-memory state transitions (`PlayAsync`, `PauseAsync`) are fast synchronous awaits and take no token. Prefer `await` over blocking calls.
- **Frame borrowing.** In `VideoFrameAvailable` / `AudioDataAvailable` callbacks you may only *read* the supplied frame and must copy any data you need synchronously; never `Dispose` or hold the reference after the callback returns.
- **AOT publishing.** Because the library is `IsAotCompatible`, host applications can publish as NativeAOT. Native decoder/renderer libraries (e.g. FFmpeg/LibVLC) must be deployed alongside the published output.
- **Logging.** LingFan.Media depends only on `Microsoft.Extensions.Logging.Abstractions`; the host application supplies the concrete `ILoggerFactory`.

## License

Licensed under the **Apache License, Version 2.0** — see [LICENSE](LICENSE). Third-party LGPL components (FFmpeg, LibVLC) are covered in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
