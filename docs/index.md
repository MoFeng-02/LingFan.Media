---
layout: home
title: LingFan.Media
sources: ["README.zh.md", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs"]
symbols: ["IMediaPlayer", "IFrameChannel", "IFrameSink", "IFrameResource"]
lang: en
hero:
  name: LingFan.Media
  text: .NET 10 AOT Media Infrastructure
  tagline: A contract-driven, headless-first, cross-platform playback pipeline — pluggable backends, GPU zero-copy, and AOT-ready native interop.
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: Architecture
      link: /guide/architecture
    - theme: alt
      text: API Reference
      link: /api/
features:
  - title: AOT-first
    details: Zero reflection activation, no ComImport, source-generated LibraryImport for every native call. Every library declares IsAotCompatible.
  - title: Pluggable backends
    details: FFmpeg, LibVLC, Media Foundation, MediaCodec and AVFoundation behind one contract and one frame-routing path. Swap a backend with a single DI registration.
  - title: Headless by default
    details: The same pipeline drives servers, off-screen jobs and UIs. Decoded frames flow through IFrameChannel / IFrameSink; a renderer is just another subscriber.
  - title: GPU zero-copy
    details: Hardware-decoded frames travel as IFrameResource GPU resources where the platform supports import, with a correct CPU fallback elsewhere.
---
