---
layout: home

hero:
  name: LingFan.Media
  text: .NET 10 AOT 媒体基础设施
  tagline: .NET 平台的跨平台、AOT 优先、可插拔媒体基础设施。
  actions:
    - theme: brand
      text: 快速开始
      link: /zh/guide/introduction
    - theme: alt
      text: 架构
      link: /zh/guide/architecture

features:
  - title: AOT 优先
    details: 零反射、零 [ComImport]、仅 [LibraryImport] 的 P/Invoke。在 NativeAOT 发布下行为确定。
  - title: 可插拔后端
    details: FFmpeg / VLC / MediaFoundation 统一在单一契约层与单一帧路由原语之后。没有按后端分叉的代码路径。
  - title: 默认无头
    details: 帧数据流经 IFrameChannel / IFrameSink。UI 只是订阅的 Sink——无头与有头使用同一套 API。
  - title: GPU 零拷贝
    details: 视频帧以 IFrameResource（CPU 或 GPU）形式传递。零拷贝是一种 Sink 能力，而非独立的代码路径。
---

## 关于 LingFan.Media

LingFan.Media 是 .NET 平台的跨平台媒体基础设施，构建于 .NET 10 之上，目标是 100% AOT 兼容。

> 完整参考： [契约层（Abstractions）](/zh/api/abstractions/) · [基础设施层](/zh/api/infrastructure/)。
