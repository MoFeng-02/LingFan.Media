# Introduction

**LingFan.Media** is a pluggable, cross-platform media infrastructure for the .NET platform — a .NET 10, AOT-first, cross-platform media foundation, designed from the ground up for managed-code AOT deployment (NativeAOT publishing).

## Why another media library?

Most .NET media stacks are thin wrappers over a single native backend (typically FFmpeg) and leak native concepts into your application. LingFan.Media takes a different stance:

- **One contract layer, many backends.** `FFmpeg`, `VLC`, and `MediaFoundation` all sit behind the same `Abstractions` interfaces. Your code never branches on *which* backend is active.
- **Headless by default.** Frames are delivered as plain data through `IFrameChannel` / `IFrameSink`. A UI control is just another subscribing Sink — the same API drives a server-side transcode, a computer-vision pipeline, or an on-screen player.
- **AOT without compromises.** Zero reflection, zero `[ComImport]`, `[LibraryImport]`-only P/Invoke, sealed types, `ValueTask` hot paths. It publishes cleanly as a NativeAOT binary.
- **GPU zero-copy as a capability, not a fork.** A video frame is an `IFrameResource` that may be CPU or GPU memory. Zero-copy presentation is decided by what the consuming Sink can do, never by a separate code path.

## Platform scope

| Phase | Platform | Backends | GPU | Audio |
|-------|----------|----------|-----|-------|
| **V1 (supported)** | Windows | Media Foundation ✅, FFmpeg ✅, VLC ✅ | D3D11 (+ DirectComposition) | WASAPI |
| Next (planned) | macOS, iOS, Android | FFmpeg ✅, VLC ✅ now; AVFoundation / MediaCodec (planned) | — | — |
| **Excluded** | Linux | FFmpeg / VLC usable (no native backend) | — | — |

V1 is the only platform with a supported, tested surface (Windows + D3D11 + WASAPI). macOS / iOS / Android already work today through the LGPL-cross-platform FFmpeg / LibVLC shared libraries; their first-party native backends (AVFoundation, MediaCodec) will be integrated progressively over time. **Linux is excluded from the native-backend roadmap** — it has no standard first-party media API, so no native Linux backend will be built; however, FFmpeg / LibVLC still provide playback there, so Linux is simply not a targeted or tested surface.

> **Not in scope:** WebRTC and GStreamer backends are explicitly out of scope (they exist only as empty scaffolding / stubs). The **Vulkan** renderer is validated for the FFmpeg zero-copy path on Windows but is not part of the V1 supported surface; OpenGL / Metal remain partials.

## Package layout (12 logical modules)

| # | Module | Role |
|---|--------|------|
| 01 | `Abstractions` | The contract layer — zero implementation, zero external references |
| 02 | `Core` | Playback logic: `MediaPlayer`, pipelines, clock, synchronizer |
| 03 | `Sources` | Media sources: file / network / stream |
| 04 | `Formats` | Container parsing & format detection |
| 05 | `Video` | Video domain: track, processor chain, stats |
| 06 | `Audio` | Audio domain: mixer, volume, effects, stats |
| 07 | `Backends` | Pluggable backends: FFmpeg / VLC / MediaFoundation (WebRTC stub) |
| 08 | `Renderers` | GPU renderers: D3D11 (real); Vulkan (validated, FFmpeg zero-copy, Windows) / Metal / OpenGL (partials) |
| 09 | `Outputs` | Audio outputs: WASAPI, OpenAL, OpenSL ES, AAudio, … |
| 10 | `Platforms` | Platform capability detection & interop |
| 11 | `Avalonia` | UI presentation: `VideoView`, `MediaControl`, Skia / Composition presenters |
| 12 | `Extensions` | DI entry point: `AddLingFanMedia()`, `MediaBuilder`, codec registry |

> The infrastructure-layer documentation in this site currently covers modules **01, 02, 03, 04, 05, 06, 12** and the `Consumers` / `Playback` helper projects. Backend, renderer, output, platform, and UI details are documented separately.

## Where to go next

- [Getting Started](/guide/getting-started) — register services and play your first file in 10 lines.
- [Architecture](/guide/architecture) — how the layers fit together and why frames route the way they do.
- [Design Philosophy](/guide/design-philosophy) — the ten principles that govern every decision.
- [Async & Sync Discipline](/guide/async-sync) — the guidelines that keep the pipeline correct under AOT.
