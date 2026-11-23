---
title: 快速开始
sources: ["src/Infrastructure/LingFan.Media.Extensions/ServiceCollectionExtensions.cs", "src/Infrastructure/LingFan.Media.Extensions/ModuleExtensions.cs", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Sources/Sources/FileMediaSource.cs"]
symbols: ["IMediaPlayer", "IMediaPlayerFactory", "IMediaSource", "FileMediaSource", "VideoFrame", "AudioFrame", "MediaState"]
lang: zh
---

# 快速开始

本页演示如何注册媒体服务，并以三种形态播放文件：有头 UI、无头消费者，以及后端回退装配。

## 1. 注册服务

LingFan.Media 通过依赖注入装配。先加入核心，再加入所需的后端、渲染器与音频输出。注册顺序对回退有意义（见下文）。

```csharp
using LingFan.Media.Extensions;                 // AddLingFanMedia
using LingFan.Media.Backends.FFmpeg;            // AddFFmpeg
using LingFan.Media.Backends.MediaFoundation;   // AddMediaFoundation
using LingFan.Media.Renderers.D3D11;            // AddD3D11Renderer
using LingFan.Media.Outputs.Wasapi;             // AddWasapiOutput
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddLingFanMedia()        // 核心：时钟、缓冲、帧路由、会话
        .AddFFmpeg()              // 跨平台解码后端（主用）
        .AddMediaFoundation()     // Windows 原生后端（可选回退）
        .AddD3D11Renderer()       // Windows GPU 渲染器
        .AddWasapiOutput();       // Windows 音频输出

var provider = services.BuildServiceProvider();
```

`AddLingFanMedia()` 挂载共享基础设施（流工厂、格式探测器、编解码注册表、带 SSRF 防护的命名 `HttpClient`）以及后端回退播放器工厂。每个 `AddXxx()` 注册该组件的工厂；解封装工厂以有序集合注册，使播放器可按序尝试。

> 请选择与目标平台匹配的渲染器与音频输出。完整矩阵见[平台支持](/zh/guide/platforms)。例如在 Linux 上，应使用 Vulkan 或 OpenGL 渲染器搭配 OpenAL 输出，而非 D3D11 / WASAPI。

## 2. 无头播放

无头消费者直接订阅解码帧——没有窗口，没有 UI 框架。这与 UI 使用的是同一条管线。

```csharp
using LingFan.Media.Abstractions;
using LingFan.Media.Sources;

var factory = provider.GetRequiredService<IMediaPlayerFactory>();
await using var player = factory.Create();

// 帧在回调内只读借用。需要同步拷贝所需数据；
// 返回后切勿 Dispose 该帧，也不要持有引用。
player.VideoFrameAvailable += (VideoFrame frame) =>
{
    // 例如读取 frame.Width / frame.Height、拷贝像素、跑推理……
};

player.StateChanged += (_, e) => Console.WriteLine(e.State);

await player.OpenAsync(new FileMediaSource("path/to/video.mp4"));
await player.PlayAsync();

// ……播放……

await player.StopAsync();
```

关键生命周期规则：

- `OpenAsync` / `SeekAsync` / `StopAsync` 执行真实 I/O，接受 `CancellationToken`。`PlayAsync` / `PauseAsync` 是纯内存态切换，同步完成。
- 传给 `VideoFrameAvailable`（及 `AudioDataAvailable`）的帧是**只读借用**。在回调返回前拷贝所需数据；切勿释放它或跨线程持有。
- 优先 `await player.DisposeAsync()`；同步 `Dispose()` 是兜底，释放资源但不等待管线线程。

## 3. 有头播放（Avalonia）

使用 Avalonia UI 层时，把播放器绑定到 `VideoView`。请**先**打开播放器再赋值，因为属性变更回调是同步 `void`，无法 await。

```csharp
await player.OpenAsync(new FileMediaSource("path/to/video.mp4"));
myVideoView.Player = player;   // VideoView 订阅帧通道并呈现
await player.PlayAsync();
```

`VideoView` 与无头消费者订阅同一帧通道，并经平台合成器呈现帧——不存在第二条分歧的投递路径。

## 4. 后端回退

注册了多个后端时，默认工厂按注册顺序尝试，并记住某个 `(容器, 编解码)` 组合下成功的后端，后续打开直接跳到可用后端。

```csharp
services.AddLingFanMedia()
        .AddFFmpeg()            // 先尝试
        .AddMediaFoundation();  // FFmpeg 无法打开时尝试
```

若要无视回退强制指定后端，使用接受后端组的 `IMediaPlayerFactory.Create(...)` 显式重载。

## 关键类型

| 类型 | 职责 |
|---|---|
| `IMediaPlayer` | 播放会话：打开 / 播放 / 暂停 / 定位 / 停止 / 释放，状态与位置，帧事件。 |
| `IMediaPlayerFactory` | 创建播放器会话；解析后端组。 |
| `IMediaSource` | 描述媒体来源（文件、网络、流）。 |
| `VideoFrame` / `AudioFrame` | 经帧通道 / 音频输出投递的解码帧。 |
| `MediaState` | 会话状态机（Idle、Opening、Buffering、Playing、Paused、Stopped、Ended、Error）。 |

上述每个公共类型在 [API 参考](/zh/api/) 下都有参考页，含精确签名与备注。
