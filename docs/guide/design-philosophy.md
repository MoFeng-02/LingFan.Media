---
title: Design Philosophy
sources: ["Directory.Build.props", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Core/Clock/MediaClock.cs", "src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.Dispose.cs"]
symbols: ["IMediaPlayer", "IFrameResource", "IVideoRenderer", "IAudioOutput", "IMediaClock", "IMediaSession", "VideoFrame"]
lang: en
---

# Design Philosophy

These principles explain why the code is shaped the way it is. They are binding
constraints, not aspirations — new code is expected to conform.

## Ten principles

1. **DI-driven assembly.** Components are registered explicitly:
   `AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`. Wiring is
   visible and controllable, not hidden behind reflection or auto-scanning.

2. **AOT-friendly.** Zero reflection activation, sealed types, `ValueTask` on hot
   paths, and compile-time-determined types. All native calls use source-generated
   `[LibraryImport]` (never `[DllImport]`, whose runtime marshalling is not
   NativeAOT-compatible). Every library declares `IsAotCompatible`; the trim and
   AOT analyzers are enabled and warnings are treated as errors.

3. **Replaceable backends.** FFmpeg, LibVLC, Media Foundation, MediaCodec and
   AVFoundation are interchangeable behind the same contracts. Adding a backend
   means adding an implementation plus one registration line.

4. **Not bound to any UI framework.** The core knows nothing about Avalonia (or
   any other UI). Only the optional UI layer depends on a framework, and it
   depends on the core — never the reverse.

5. **GPU zero-copy.** Decoded frames are carried as `IFrameResource`, unifying CPU
   and GPU resources. Hardware-decoded frames stay on the GPU where the platform
   supports import; elsewhere a correct CPU fallback is used.

6. **Headless rendering.** The library never creates a standalone native window.
   Frames are composed into the host's surface through the platform compositor
   (DirectComposition on Windows, CAMetalLayer on Apple platforms, SurfaceFlinger
   on Android, Wayland subsurfaces on Linux). Each platform has its own correct
   implementation behind one `IVideoRenderer` contract. Linux is a supported
   target with a validated FFmpeg + VAAPI + Vulkan zero-copy path.

7. **Memory safety.** Frame ownership transfers explicitly; arrays are pooled;
   native resources use `SafeHandle` plus layered `Dispose`. No dangling
   references: a borrowed frame must not outlive its callback.

8. **Synchronous pipelines, async I/O boundaries.** Pipeline control methods
   (start / pause / stop / flush) are synchronous and in-memory; `Stop` only
   signals cancellation. Real asynchronous work lives at I/O boundaries (open,
   seek, read, network) and in graceful disposal, which joins pipeline threads
   with a bounded timeout. See [Async & Sync Discipline](/guide/async-sync).

9. **Cancellation tokens at the edges.** `CancellationToken` is accepted at I/O
   and session-lifetime boundaries (`OpenAsync`, `SeekAsync`, `StopAsync`,
   packet reads), not on hot paths such as decode / present / submit.

10. **Session isolation.** Each `IMediaPlayer` has its own clock, buffer and
    pipelines. DI provides system-level factories; session objects are created
    per open. Concurrent players never share mutable playback state.

## Contract-layer evolution

`Abstractions` is the law of the system, but it is not frozen. When a genuine
need arises, new method signatures may be added to interfaces and models. This
avoids two failure modes: a missing async signature forcing callers to block
synchronously, or a missing sync signature forcing awkward detours at native
boundaries that cannot await.

Every addition must satisfy four hard rules:

| Rule | Meaning |
|---|---|
| Zero external references | Signatures may only use BCL types or types already defined in `Abstractions`. No backend, renderer or UI type may appear in a contract signature. |
| Zero implementation | Only signatures, auto-properties and pure data models. No business logic, no constructing concrete implementations. |
| Async/sync by nature | Whether a new signature is async or sync follows the real-I/O rule, not convenience. |
| Add, don't change | Prefer adding signatures over breaking existing ones. If a signature must change, every implementation and caller is reviewed together. |

A neutral `IDisposable?` owner field, for example, lets a backend hand back native
frame ownership without the contract layer ever naming a backend-specific handle
type. This is dependency inversion paying off: as long as the contract layer
stays pure, backends, renderers, outputs and UI can be replaced freely while
downstream layers adapt around stable contracts.
