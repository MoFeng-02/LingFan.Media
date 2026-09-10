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
| **V1（受支持）** | Windows | Media Foundation ✅、FFmpeg ✅、VLC ✅ | D3D11（+ DirectComposition）、Vulkan ✅（零拷贝） | WASAPI |
| **V1.5（受支持，跨平台后端路线）** | Linux | FFmpeg ✅（VAAPI 硬解 ✅，已实测）、VLC ✅（已实现，Linux 待验证） | Vulkan ✅（零拷贝）、OpenGL ✅（零拷贝受显示支持面限制，自动回落 CPU 上传） | OpenAL ✅ |
| **V1.x（受支持，真机实测）** | Android | MediaCodec ✅（真机）；FFmpeg ✅、VLC ✅（已实现） | Vulkan/GLES ✅（AHB 零拷贝，真机） | OpenSL ES / AAudio ✅（已实现） |
| **暂缓（缺设备）** | macOS, iOS | FFmpeg ✅、VLC ✅（已实现）；AVFoundation（部分实现就绪，待设备） | Metal 渲染器等已部分实现 | AVAudioEngine / AudioUnit（已部分实现） |

V1 是第一个受支持、经测试的表面（Windows + D3D11 + WASAPI）。**Linux 已在跨平台后端路线上实测通过**：FFmpeg 后端经 VAAPI 硬解，导出的 dma_buf 由 Vulkan 渲染器零拷贝上屏（Intel iGPU 实测），OpenGL 渲染器在 Mesa 支持面不足时自动回落 CPU 上传（画面始终正确），音频经 OpenAL 输出。**Android 已真机实测**：MediaCodec 硬解经 Surface/AHB 输出，由 Skia GPU 渲染器零拷贝采样上屏，音频经 OpenSL ES / AAudio 输出。**macOS / iOS 暂缓——缺设备，暂时无法实现与测试（请等待）**：FFmpeg / LibVLC 跨平台库本身可用，Metal 渲染器、AVAudioEngine / AudioUnit 音频等已部分实现，待有设备后继续。**Linux 不设原生后端**——它没有标准的第一方媒体 API，播放全部经 FFmpeg / LibVLC 跨平台后端实现。

> **范围之外：** WebRTC 与 GStreamer 后端明确不在范围内（仅以空脚手架 / 存根形式存在）。**Vulkan** 渲染器已在 Windows 与 Linux 的 FFmpeg 零拷贝路径上验证；OpenGL / Metal 仍为部分实现。

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
| 08 | `Renderers` | GPU 渲染器：D3D11（实装）；Vulkan（已验证，Windows / Linux 零拷贝）/ OpenGL（已实现）/ Metal（部分） |
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
