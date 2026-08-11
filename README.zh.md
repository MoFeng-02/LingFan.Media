<p align="center">
  <img src="logo/LingFan.png" alt="LingFan Media" width="220" />
</p>

# LingFan.Media（灵泛）

> 英文文档：[README.md](README.md)

**LingFan.Media（灵泛媒体）** 是 .NET 平台的跨平台媒体基础设施（独立项目，非灵泛引擎子模块/衍生）。它提供一套模块化、DI 友好、AOT 就绪的抽象层，把核心播放逻辑与具体引擎（解码器、解封装器、渲染器、音频输出）解耦，使这些组件可以按平台或部署环境自由替换。

> 状态：本库基于 **.NET 10** 活跃开发中。当前首要验证目标是 **Windows**；Linux 支持已通过 FFmpeg 与 LibVLC 后端落地，其余平台在路线图内（见[平台与后端状态](#平台与后端状态)）。它尚不是一个功能完备、覆盖所有平台的媒体框架——但整体设计是为了在不破坏公开 API 的前提下逐步走到那里。**目前仅本地文件播放已端到端验证；网络源与流式播放虽已实现，但尚未在运行时验证。**

## 为什么再写一层媒体抽象

- **模块化，而非单体。** 后端、渲染器、音频输出都是经依赖注入注册的独立组件。新增或替换其中一个，不会触及核心代码。
- **DI 驱动的装配。** 你按需拼装出刚好需要的管线（`AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`），而不是引入一个固定的引擎。
- **AOT 就绪。** 代码库以 `net10.0` + `IsAotCompatible=true` 为目标，避免基于反射的激活与 `ComImport`，原生互操作走源生成的 P/Invoke（`[LibraryImport]`），因此可发布为 NativeAOT 二进制。
- **无头优先（headless-first）。** 同一条播放管线既可驱动服务端 / 离屏处理，也可驱动上屏渲染。视频帧经由唯一的帧通道投递：无头消费者订阅帧，UI 渲染器把帧呈现给平台合成器。
- **契约层保持纯净。** 上层只依赖 `Abstractions` 契约；具体的后端与渲染器通过注入引入，保留依赖倒置。

## 平台与后端状态

| 平台 | 状态 | 可用后端 |
| --- | --- | --- |
| **Windows** | 已支持（首要验证目标） | MediaFoundation（原生、可硬件解码）、FFmpeg、LibVLC |
| **Linux** | 已通过 FFmpeg + LibVLC 落地，配合 Vulkan / OpenGL 渲染器；验证进行中 | FFmpeg、LibVLC |
| **macOS / iOS / Android** | 路线图——架构上可容纳，但尚未验证 | — |

所有后端共享同一套可插拔模型，因此无论选中哪个引擎，`IMediaPlayer` 的接口形态都一致。后端选择在运行时依据你注册的内容解析。

> 注：WebRTC / GStreamer 明确不在当前范围内。

## 当前成熟度

本库各能力的完成度并不一致。下表按能力逐一标明：是否已端到端验证、已实现但尚未运行时验证、正在验证、在路线图内，或明确不在范围。

**成熟度进程：** V1 Windows（已验证） → 多后端（已验证） → Linux 验证（进行中） → macOS / iOS / Android（路线图）。WebRTC 与 GStreamer 不在范围内。

| 能力 | 状态 |
| --- | --- |
| 本地文件播放（Windows） | **已验证** |
| D3D11 渲染器（Windows） | **已验证** |
| WASAPI 音频输出（Windows） | **已验证** |
| 无头帧投递（帧通道） | **已验证** |
| MediaFoundation 后端 | **已验证** |
| FFmpeg 后端 | **已验证** |
| LibVLC 后端 | **已验证** |
| 网络源（`NetworkMediaSource` + SSRF） | 已实现，待验证 |
| 流式播放 | 已实现，待验证 |
| Linux（FFmpeg + LibVLC + Vulkan / OpenGL） | **验证中** |
| Vulkan / OpenGL 渲染器 | **验证中** |
| macOS / iOS / Android | 路线图 |
| WebRTC / GStreamer | 不在范围 |

> 已验证的 Windows 路径在本地文件上实测了核心抽象、渲染、音频输出与无头帧投递。网络源与流式播放虽已实现（含基于 DNS-pinning 的 SSRF 防护），但尚未端到端实测——在运行时验证之前，请按实验性对待。

> **硬件解码说明：** 在 Windows 上，MediaFoundation 的解码器会把帧经由 CPU 内存交回（混合解码 / 半硬解）。这是平台 MFT 管线的固有特性，并非本库的缺陷。FFmpeg 与 LibVLC 后端则提供完整的 GPU 驻留硬件解码路径。三种后端均可在 Windows 上正常播放本地文件。

## 安装

LingFan.Media 由本仓库构建。生产库通过 `dotnet pack` 打包（Apache-2.0，第三方 LGPL 义务如 FFmpeg、LibVLC 见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)）。

按需引用所需项目，或在你的应用中消费产出的 NuGet 包。

## 快速开始

```csharp
using Microsoft.Extensions.DependencyInjection;
using LingFan.Media.Abstractions;        // IMediaPlayer, IMediaPlayerFactory, IMediaSource
using LingFan.Media.Extensions;          // AddLingFanMedia
using LingFan.Media.Backends.FFmpeg;     // AddFFmpeg
using LingFan.Media.Renderers.D3D11;     // AddD3D11Renderer
using LingFan.Media.Outputs.WASAPI;      // AddWasapiOutput
using LingFan.Media.Sources;             // FileMediaSource

// 1. 装配管线（后端 + 渲染器 + 音频输出）。
var services = new ServiceCollection();
services.AddLingFanMedia()
        .AddFFmpeg()
        .AddD3D11Renderer()
        .AddWasapiOutput();
var provider = services.BuildServiceProvider();

// 2. 解析一个播放器。回退工厂会按注册情况自动选择可用后端。
var factory = provider.GetRequiredService<IMediaPlayerFactory>();
using var player = factory.Create();

// 3. 无头模式：订阅帧。（上屏渲染则改为挂载 UI 呈现器。）
player.VideoFrameAvailable += (VideoFrame frame) =>
{
    // 在回调内对帧做只读借用。
    // 不要 Dispose，也不要跨线程持有该引用。
};

// 4. 打开并播放。
await player.OpenAsync(new FileMediaSource(@"C:\videos\clip.mp4"));
await player.PlayAsync();

// 播放过程中的控制：
await player.PauseAsync();
await player.SeekAsync(TimeSpan.FromSeconds(30));

// 结束时：
await player.StopAsync();
await player.DisposeAsync();
```

### 显式选择后端

`AddMediaFoundation()`（Windows）、`AddFFmpeg()`、`AddVLCNative()` 会把各自的工厂注册进 DI 容器。当注册了多个后端时，运行时按注册顺序选择一个；若某后端无法处理该源，则回退到下一个。要强制指定某个后端，可使用接受显式后端组的 `IMediaPlayerFactory.Create(...)` 重载。

### 网络源（实验性，尚未验证）

`NetworkMediaSource` 已实现，并内置基于 DNS-pinning 的 SSRF 防护：构造期拒绝私有/回环/链路本地/保留(CGNAT)地址，且连接时对解析出的 IP 做 pinning，使重定向后的 URL 无法落到内网地址。但**网络源与流式播放尚未端到端验证**——目前只测过本地文件播放。在运行时验证之前，请将其视为实验性功能。

## 架构简述

```
┌─────────────────────────────────────────────┐
│  Abstractions（契约：IMediaPlayer、          │
│  IMediaSource、IFrameChannel、帧模型）       │  ← 零外部引用
├─────────────────────────────────────────────┤
│  Core / Playback（编排、时钟、               │
│  帧路由、会话生命周期）                      │
├──────────────┬───────────────┬───────────────┤
│  Backends    │  Renderers    │  Audio Outputs │  ← 可插拔，DI 注册
│ (MF/FFmpeg/  │ (D3D11/      │  (WASAPI /     │
│  VLC)        │  Vulkan/GL)  │   headless)    │
└──────────────┴───────────────┴───────────────┘
```

- **帧路由统一**：每个解码出的视频帧都流经同一条帧通道。无头消费者通过 `VideoFrameAvailable` 事件借用帧；UI 渲染器把帧呈现给平台合成器。不存在第二条分歧投递路径。
- **会话隔离**：每个 `IMediaPlayer` 拥有独立的会话（时钟、缓冲、管线）。基础设施工厂为单例；会话按播放器各自创建。
- **处理模式**：`Mode = ProcessingMode.Fastest` 会关闭音视频同步与实时节流，用于批处理 / 离线场景（转码、ML 推理）；`RealTime` 为正常播放的默认模式。

## 使用注意事项

- **异步 / 同步分界。** 含 I/O 的操作暴露异步签名（`OpenAsync`、`StopAsync`、`SeekAsync`）并接受 `CancellationToken`。纯内存态切换（`PlayAsync`、`PauseAsync`）是快速的同步 await，不接收令牌。优先使用 `await` 而非阻塞调用。
- **帧借用。** 在 `VideoFrameAvailable` / `AudioDataAvailable` 回调中你只能*读取*所给帧，并须同步拷贝所需数据；回调返回后切勿 `Dispose` 或持有该引用。
- **AOT 发布。** 由于本库 `IsAotCompatible`，宿主应用可发布为 NativeAOT。原生解码器 / 渲染器库（如 FFmpeg / LibVLC）须随发布产物一同部署。
- **日志。** LingFan.Media 仅依赖 `Microsoft.Extensions.Logging.Abstractions`；具体 `ILoggerFactory` 由宿主应用提供。

## 许可证

基于 **Apache License, Version 2.0** 授权——见 [LICENSE](LICENSE)。第三方 LGPL 组件（FFmpeg、LibVLC）的合规说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
