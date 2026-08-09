---
layout: home

hero:
  name: LingFan.Media
  text: .NET 10 AOT Media Infrastructure
  tagline: A cross-platform, AOT-first, pluggable media infrastructure for the .NET platform.
  actions:
    - theme: brand
      text: Get Started
      link: /guide/introduction
    - theme: alt
      text: Architecture
      link: /guide/architecture

features:
  - title: AOT-first
    details: Zero reflection, zero [ComImport], [LibraryImport]-only P/Invoke. Deterministic under NativeAOT publishing.
  - title: Pluggable backends
    details: FFmpeg / VLC / MediaFoundation behind one contract layer and one frame-routing primitive. No per-backend branches.
  - title: Headless by default
    details: Frame data flows through IFrameChannel / IFrameSink. UI is just a subscribing Sink — same API for headless and headed.
  - title: GPU zero-copy
    details: Video frames travel as IFrameResource (CPU or GPU). Zero-copy is a Sink capability, not a separate code path.
---

## About LingFan.Media

LingFan.Media is a cross-platform media infrastructure for the .NET platform, built on .NET 10 with a 100% AOT compatibility target.

> Full references: [Contract Layer (Abstractions)](/api/abstractions/) · [Infrastructure Layer](/api/infrastructure/).
