# 简介

**LingFan.Media** 是一个可插拔的跨平台媒体基础设施——基于 .NET 10、AOT 优先的跨平台媒体基础，从一开始就为托管代码 AOT 部署（NativeAOT 发布）而设计。

## 为什么还需要一个媒体库？

大多数 .NET 媒体栈只是对单一原生后端（通常是 FFmpeg）的薄封装，并把原生概念泄漏到你的应用中。LingFan.Media 采取了不同的立场：

- **单一契约层，多种后端。** `FFmpeg`、`VLC` 与 `MediaFoundation` 都位于同一组 `Abstractions` 接口之后。你的代码从不需要按"当前用的是哪个后端"来分叉。
- **默认无头。** 帧以纯数据形式通过 `IFrameChannel` / `IFrameSink` 投递。UI 控件只是另一个订阅的 Sink——同一套 API 既能驱动服务端转码、计算机视觉管线，也能驱动屏幕上的播放器。
- **AOT 不妥协。** 零反射、零 `[ComImport]`、仅 `[LibraryImport]` 的 P/Invoke、sealed 类型、`ValueTask` 热路径。它可以干净地作为 NativeAOT 二进制发布。
- **GPU 零拷贝是一种能力，而非分叉。** 视频帧是一个 `IFrameResource`，既可以是 CPU 也可以是 GPU 内存。是否零拷贝呈现由消费方 Sink 的能力决定，绝不由独立的代码路径决定。

## 平台范围

| 阶段 | 平台 | 后端 | GPU | 音频 |
|------|------|------|-----|------|
| **V1（受支持）** | Windows | Media Foundation ✅、FFmpeg ✅、VLC ✅ | D3D11 (+ DirectComposition) | WASAPI |
| 下一阶段（计划） | macOS, iOS, Android | FFmpeg ✅、VLC ✅（现已可用）；AVFoundation / MediaCodec（计划中） | — | — |
| **已排除** | Linux | FFmpeg / VLC 可用（无原生后端） | — | — |

V1 是唯一具备受支持、经测试表面的平台（Windows + D3D11 + WASAPI）。macOS / iOS / Android 今天已可借助本身即 LGPL 跨平台的 FFmpeg / LibVLC 共享库工作；其第一方原生后端（AVFoundation、MediaCodec）将随时间逐步集成。**Linux 被排除在原生后端路线之外**——它没有标准的第一方媒体 API，故不会构建原生 Linux 后端；不过 FFmpeg / LibVLC 仍可在那里提供播放，因此 Linux 只是不被作为目标或已测试的表面。

> **范围之外：** WebRTC 与 GStreamer 后端明确不在范围内（仅以空脚手架 / 存根形式存在）。Vulkan / OpenGL / Metal 渲染器为存根 / 部分实现，不属于 V1 受支持表面。

## 包结构（12 个逻辑模块）

| # | 模块 | 职责 |
|---|--------|------|
| 01 | `Abstractions` | 契约层——零实现、零外部引用 |
| 02 | `Core` | 播放逻辑：`MediaPlayer`、管线、时钟、同步器 |
| 03 | `Sources` | 媒体源：文件 / 网络 / 流 |
| 04 | `Formats` | 容器解析与格式探测 |
| 05 | `Video` | 视频域：轨道、处理器链、统计 |
| 06 | `Audio` | 音频域：混音、音量、效果、统计 |
| 07 | `Backends` | 可插拔后端：FFmpeg / VLC / MediaFoundation（WebRTC 桩） |
| 08 | `Renderers` | GPU 渲染器：D3D11（实装）、Vulkan / Metal / OpenGL（桩/部分） |
| 09 | `Outputs` | 音频输出：WASAPI、OpenAL、OpenSL ES、AAudio，… |
| 10 | `Platforms` | 平台能力探测与互操作 |
| 11 | `Avalonia` | UI 呈现：`VideoView`、`MediaControl`、Skia / Composition 呈现器 |
| 12 | `Extensions` | DI 入口：`AddLingFanMedia()`、`MediaBuilder`、编解码器注册表 |

> 本站的基础设施层文档目前覆盖模块 **01、02、03、04、05、06、12** 以及 `Consumers` / `Playback` 辅助项目。后端、渲染器、输出、平台与 UI 的细节另行文档化。

## 下一步去哪

- [快速开始](/zh/guide/getting-started) — 注册服务，10 行代码播放你的第一个文件。
- [架构](/zh/guide/architecture) — 各层如何组合，以及帧为何这样路由。
- [设计哲学](/zh/guide/design-philosophy) — 支配每个决策的十条原则。
- [异步与同步纪律](/zh/guide/async-sync) — 在 AOT 下保持管线正确的准则。
