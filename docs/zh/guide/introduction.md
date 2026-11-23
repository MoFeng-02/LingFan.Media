---
title: 简介
sources: ["README.zh.md", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.cs"]
symbols: ["IMediaPlayer", "IMediaSource", "IFrameChannel", "IFrameSink", "IVideoRenderer", "IAudioOutput"]
lang: zh
---

# 简介

LingFan.Media 是面向 .NET 10 的跨平台媒体基础设施。它提供一条统一的、契约驱动的播放管线——解封装、解码、时钟同步、帧路由、渲染与音频输出——在服务端、离屏批处理任务以及桌面或移动 UI 中以同一套方式运行。

它的设计目标是 **AOT 优先**、**无头优先**，并在每一个边界上**可插拔**：解码后端、GPU 渲染器与音频输出各自是通过依赖注入注册的独立组件，替换其中任何一个都不会触及核心。

## 它是什么

- 一组以纯契约层（`LingFan.Media.Abstractions`）为中心、可打包为 NuGet 的库。契约层零实现，除 BCL 与日志抽象外零外部引用。
- 一个核心编排层（`LingFan.Media.Core`），拥有播放时钟、缓冲、帧路由与会话生命周期，不感知任何具体后端或 UI 框架。
- 可插拔的后端（FFmpeg、LibVLC、Media Foundation、MediaCodec、AVFoundation）、渲染器（D3D11、Vulkan、OpenGL、OpenGL ES、Metal）与音频输出（WASAPI、OpenAL、OpenSL ES、AAudio、AVAudioEngine），各自经 DI 接入。

公共入口是 `IMediaPlayer`。你打开一个 `IMediaSource`，播放、暂停、定位、释放；解码后的视频帧经唯一的帧通道（`IFrameChannel` / `IFrameSink`）投递，对大多数调用方表现为 `IMediaPlayer.VideoFrameAvailable` 事件。

## 设计目标

| 目标 | 实际含义 |
|---|---|
| 模块化 | 后端、渲染器、音频输出是各自独立的 DI 注册组件；替换其一仅改动一行注册。 |
| AOT 就绪 | `net10.0` 且 `IsAotCompatible`，零反射激活，无 `ComImport`，所有原生调用使用源生成的 `[LibraryImport]`。 |
| 无头优先 | 同一管线驱动服务端与离屏任务；UI 渲染器只是帧通道的又一个订阅者。 |
| 契约纯净 | 上层只依赖 `Abstractions`；依赖倒置使核心保持平台无关。 |
| GPU 零拷贝 | 硬解帧在平台支持导入时以 `IFrameResource` GPU 资源传递，其余情形有正确的 CPU 回落。 |

## 平台范围

LingFan.Media 面向 Windows、Linux、Android、macOS 与 iOS。各平台的支持程度不同，并集中在一处维护——权威的后端 / 渲染器 / 音频 / 零拷贝矩阵见[平台支持](/zh/guide/platforms)。本页刻意不复制该矩阵。

## 接下来读什么

- [快速开始](/zh/guide/getting-started)——注册服务并播放第一个文件。
- [架构总览](/zh/guide/architecture)——模块图、帧路由与会话模型。
- [设计哲学](/zh/guide/design-philosophy)——结构背后的原则。
- [异步与同步纪律](/zh/guide/async-sync)——哪里是真异步，哪里禁止异步。
- [API 参考](/zh/api/)——契约参考：接口、模型、枚举与事件。
