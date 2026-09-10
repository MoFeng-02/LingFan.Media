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
| **V1 (supported)** | Windows | Media Foundation ✅, FFmpeg ✅, VLC ✅ | D3D11 (+ DirectComposition), Vulkan ✅ (zero-copy) | WASAPI |
| **V1.5 (supported, cross-platform backend route)** | Linux | FFmpeg ✅ (VAAPI hardware decode ✅, tested), VLC ✅ (implemented, pending Linux validation) | Vulkan ✅ (zero-copy), OpenGL ✅ (zero-copy subject to display support, automatic CPU-upload fallback) | OpenAL ✅ |
| **V1.x (supported, real-device tested)** | Android | MediaCodec ✅ (real device); FFmpeg ✅, VLC ✅ (implemented) | Vulkan/GLES ✅ (AHB zero-copy, real device) | OpenSL ES / AAudio ✅ (implemented) |
| **On hold (no hardware)** | macOS, iOS | FFmpeg ✅, VLC ✅ (implemented); AVFoundation (partially implemented, awaiting hardware) | Metal renderer partially implemented | AVAudioEngine / AudioUnit (partially implemented) |

V1 was the first supported, tested surface (Windows + D3D11 + WASAPI). **Linux has now been validated along the cross-platform backend route**: the FFmpeg backend hardware-decodes through VAAPI, the exported dma_buf is imported zero-copy by the Vulkan renderer (validated on Intel iGPUs), the OpenGL renderer falls back to CPU upload automatically where Mesa support is limited (the picture always stays correct), and audio plays through OpenAL. **Android is validated on real hardware**: MediaCodec hardware decode outputs through Surface/AHB, sampled zero-copy by the Skia GPU renderer, with audio through OpenSL ES / AAudio. **macOS / iOS are on hold — no hardware is available, so implementation and testing cannot proceed for now (please wait)**: the FFmpeg / LibVLC cross-platform libraries themselves work there, and the Metal renderer plus AVAudioEngine / AudioUnit audio are partially implemented; work resumes once hardware is available. **Linux has no native backend by design** — it has no standard first-party media API, so all playback there rides the FFmpeg / LibVLC cross-platform backends.

> **Not in scope:** WebRTC and GStreamer backends are explicitly out of scope (they exist only as empty scaffolding / stubs). The **Vulkan** renderer is validated for the FFmpeg zero-copy path on both Windows and Linux; OpenGL / Metal remain partials.

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
| 08 | `Renderers` | GPU renderers: D3D11 (real); Vulkan (validated, zero-copy on Windows / Linux) / OpenGL (implemented) / Metal (partial) |
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
