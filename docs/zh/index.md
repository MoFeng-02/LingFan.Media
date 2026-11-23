---
layout: home
title: LingFan.Media
sources: ["README.zh.md", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs"]
symbols: ["IMediaPlayer", "IFrameChannel", "IFrameSink", "IFrameResource"]
lang: zh
hero:
  name: LingFan.Media
  text: .NET 10 AOT 媒体基础设施
  tagline: 契约驱动、无头优先的跨平台播放管线——可插拔后端、GPU 零拷贝、AOT 就绪的原生互操作。
  actions:
    - theme: brand
      text: 快速上手
      link: /zh/guide/getting-started
    - theme: alt
      text: 架构总览
      link: /zh/guide/architecture
    - theme: alt
      text: API 参考
      link: /zh/api/
features:
  - title: AOT 优先
    details: 零反射激活、无 ComImport，每个原生调用都用源生成的 LibraryImport。每个库声明 IsAotCompatible。
  - title: 可插拔后端
    details: FFmpeg、LibVLC、Media Foundation、MediaCodec 与 AVFoundation 在同一契约、同一帧路由路径之后。换后端只改一行 DI 注册。
  - title: 默认无头
    details: 同一管线驱动服务器、离屏任务与 UI。解码帧经 IFrameChannel / IFrameSink 流动；渲染器只是另一个订阅者。
  - title: GPU 零拷贝
    details: 硬解帧在平台支持导入时以 IFrameResource GPU 资源形态传递，其余情形使用正确的 CPU 回落。
---
