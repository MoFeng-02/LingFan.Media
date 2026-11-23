---
title: Introduction
sources: ["README.zh.md", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.cs"]
symbols: ["IMediaPlayer", "IMediaSource", "IFrameChannel", "IFrameSink", "IVideoRenderer", "IAudioOutput"]
lang: en
---

# Introduction

LingFan.Media is a cross-platform media infrastructure for .NET 10. It provides a
single, contract-driven playback pipeline — demuxing, decoding, clock
synchronization, frame routing, rendering and audio output — that runs the same
way on a server, in an off-screen batch job, and inside a desktop or mobile UI.

It is built to be **AOT-first**, **headless by default**, and **pluggable at every
boundary**: the decoding backend, the GPU renderer and the audio output are each
independent components registered through dependency injection, so replacing one
never touches the core.

## What it is

- A set of NuGet-packable libraries centered on a pure contract layer
  (`LingFan.Media.Abstractions`) that has zero implementation and zero external
  references beyond the BCL and logging abstractions.
- A core orchestration layer (`LingFan.Media.Core`) that owns the playback clock,
  buffering, frame routing and session lifetime, and knows nothing about any
  specific backend or UI framework.
- Pluggable backends (FFmpeg, LibVLC, Media Foundation, MediaCodec, AVFoundation),
  renderers (D3D11, Vulkan, OpenGL, OpenGL ES, Metal) and audio outputs (WASAPI,
  OpenAL, OpenSL ES, AAudio, AVAudioEngine), each wired in through DI.

The public entry point is `IMediaPlayer`. You open an `IMediaSource`, play, pause,
seek and dispose; decoded video frames are delivered through a single frame
channel (`IFrameChannel` / `IFrameSink`), surfaced to most callers as the
`IMediaPlayer.VideoFrameAvailable` event.

## Design goals

| Goal | What it means in practice |
|---|---|
| Modular | Backends, renderers and audio outputs are separate DI-registered components; swapping one changes a single registration line. |
| AOT-ready | `net10.0` with `IsAotCompatible`, zero reflection activation, no `ComImport`, source-generated `[LibraryImport]` for all native calls. |
| Headless-first | The same pipeline drives server-side and off-screen workloads; a UI renderer is just one more subscriber to the frame channel. |
| Pure contracts | Upper layers depend only on `Abstractions`; dependency inversion keeps the core platform-agnostic. |
| GPU zero-copy | Hardware-decoded frames travel as `IFrameResource` GPU resources where the platform supports it, with a correct CPU fallback elsewhere. |

## Platform scope

LingFan.Media targets Windows, Linux, Android, macOS and iOS. Support differs by
platform and is tracked in one place — see [Platform Support](/guide/platforms)
for the authoritative backend / renderer / audio / zero-copy matrix. This page
deliberately does not duplicate that matrix.

## Where to go next

- [Getting Started](/guide/getting-started) — register services and play your first file.
- [Architecture](/guide/architecture) — the module map, frame routing and session model.
- [Design Philosophy](/guide/design-philosophy) — the principles behind the structure.
- [Async & Sync Discipline](/guide/async-sync) — where async is real and where it is forbidden.
- [API Reference](/api/) — the contract reference: interfaces, models, enums and events.
