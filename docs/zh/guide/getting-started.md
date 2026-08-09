# 快速开始

本指南通过依赖注入接入 LingFan.Media 并播放一个文件——包含无界面（无头）与有界面（有界面）两种情形。

## 1. 注册服务

LingFan.Media 完全通过 `Microsoft.Extensions.DependencyInjection` 组装。唯一的入口是 `AddLingFanMedia()`，它返回一个流畅的 `MediaBuilder`。随后你链式调用 后端 与 渲染器/输出 的注册。

```csharp
using LingFan.Media.Extensions;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services
    .AddLingFanMedia()          // core + fallback middleware (auto-mounted)
    .AddFFmpeg()                 // FFmpeg backend (cross-platform)
    .AddMediaFoundation()        // Windows MediaFoundation backend
    .AddD3D11Renderer()          // D3D11 GPU renderer (Windows)
    .AddWasapiOutput();          // WASAPI audio output (Windows)

// 在 Apple / Android 上，FFmpeg + VLC 今天已可用；第一方原生后端
//（AVFoundation、MediaCodec）将逐步加入。Linux 不是目标平台。

var provider = services.BuildServiceProvider();
```

`AddLingFanMedia()` 会自动挂载 `BackendFallbackMediaPlayerFactory`，因此运行时会为你挑选一个可用的 后端，并在某个 后端 抛错时回退。

## 2. 打开并播放（无界面 无头）

在无界面模式下，你订阅 `VideoFrameAvailable` / `AudioDataAvailable` 并自行消费帧——无需 UI。

```csharp
using LingFan.Media.Abstractions;
using LingFan.Media.Sources;

// Resolve the factory (auto-mounted by AddLingFanMedia).
var factory = provider.GetRequiredService<IMediaPlayerFactory>();
var player = factory.Create();

// Build a source for a local file.
IMediaSource source = new FileMediaSource("sample.mp4");

player.VideoFrameAvailable += frame =>
{
    // frame is a read-only borrow — do NOT Dispose it.
    Console.WriteLine($"frame {frame.Width}x{frame.Height} @ {frame.Timestamp}");
};

await player.OpenAsync(source);
await player.PlayAsync();

// ... keep the process alive while playback runs ...
await player.StopAsync();
await player.DisposeAsync();
```

`IMediaPlayer` 暴露完整的契约：`State`、`Position`、`Duration`、`Volume`、`PlaybackRate`、`VideoDroppedFrames`，以及 `StateChanged`、`ErrorOccurred`、`PositionChanged` 与 `SubtitleReceived` 事件。

## 3. 打开并播放（有界面 有界面，Avalonia）

对于屏幕上的播放器，将 `IMediaPlayer` 绑定到 Avalonia 的 `VideoView`。UI 订阅同一个 `VideoFrameAvailable` 通道，并将其桥接到 `IVideoRenderer.Present`——无需改动 后端。

```csharp
// After AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()
// and AddAvaloniaControls() (UI layer):
var player = provider.GetRequiredService<IMediaPlayerFactory>().Create();
await player.OpenAsync(new FileMediaSource("sample.mp4"));
myVideoView.Player = player;   // VideoView subscribes to the frame channel
await player.PlayAsync();
```

## 4. 后端 选择与回退

你通常不需要挑选 后端。`BackendFallbackMediaPlayerFactory` 按 DI 注册顺序尝试各个 后端，并按文件以及按 `(container, codec)` 对记住可用的那一个。要强制使用特定 后端，可使用 `IMediaPlayerFactory.Create(...)` 重载并显式传入 解复用器/解码器 工厂。

> **开箱即不支持：** 循环播放（没有 `Loop` 属性——请订阅 `StateChanged` 并在 `Ended` 时调用 `PlayAsync()`）、播放列表，以及跨进程的持久 后端 内存。

## 关键类型

| Type | Namespace | Role |
|------|-----------|------|
| `IMediaPlayer` | `LingFan.Media.Abstractions` | 播放门面（facade） |
| `IMediaPlayerFactory` | `LingFan.Media.Abstractions` | 创建播放器；默认实现为 `BackendFallbackMediaPlayerFactory` |
| `IMediaSource` / `FileMediaSource` | `LingFan.Media.Abstractions` / `LingFan.Media.Sources` | 描述要播放的内容 |
| `AddLingFanMedia()` | `LingFan.Media.Extensions` | DI 入口 |
| `MediaBuilder` | `LingFan.Media.Extensions` | 流畅的注册链 |
