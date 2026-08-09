# 基础设施层

`LingFan.Media` 基础设施是位于契约层**之下**、平台后端（FFmpeg / MediaFoundation / VLC）、渲染器（D3D11 / Vulkan）与音频引擎（WASAPI）**之上**的**实现层**。播放图正是在这一层被实际组装起来的。

这里的全部内容都依赖 `LingFan.Media.Abstractions`，但基础设施模块本身是拆分开的，没有任何单一模块依赖某个具体后端。依赖方向始终是 **后端 → 抽象**，绝不反向。

## 模块地图

| 模块（项目） | 职责 | 关键类型 |
|------------------|------|-----------|
| `LingFan.Media.Core` | 播放编排 —— 会话对象图 | `MediaPlayer`, `MediaPlayerFactory`, `MediaClock`, `MediaSession`, `MediaPipelineHost`, `VideoPipeline`, `AudioPipeline`, `BufferManager`, `Synchronizer`, `FramePool`, `FrameChannel` |
| `LingFan.Media.Playback` | 后端回退中间件（开放盒式，异常驱动） | `BackendFallbackMediaPlayerFactory`, `FallbackMediaPlayer` |
| `LingFan.Media.Extensions` | DI 组合根 —— `AddLingFanMedia(...)` 与 `MediaBuilder` | `AddLingFanMedia`, `MediaBuilder`, `MediaOptions`, `CodecRegistry` |
| `LingFan.Media.Sources` | 源与流抽象 + SSRF 保护 | `MediaStreamFactory`, `FileMediaSource`, `NetworkMediaSource`, `StreamMediaSource`, `SsrfGuard`, `SsrfConnectGuard` |
| `LingFan.Media.Formats` | 容器探测与解复用器路由 | `DemuxerFactory`, `FormatDetector`, `FormatSignature`, `MetadataExtractor` |
| `LingFan.Media.Consumers` | 无头 / 服务端消费者 | `ConsumersExtensions`, `NoOpAudioOutput`, `NoOpVideoRenderer`, `ProcessingFrameSink`, `ProcessingAudioSink` |

## 两个组合根

- **`AddLingFanMedia(...)`**（Extensions）—— 注册该基础设施唯一受支持的方式。它装配 Singleton 工厂与 Transient 的 `IMediaPlayer`，挂载受 SSRF 保护的 HTTP 客户端，并返回 `MediaBuilder` 以便链式注册后端/渲染器/输出。
- **`BackendFallbackMediaPlayerFactory`**（Playback）—— 默认的 `IMediaPlayerFactory`。它在运行时解析*哪个*后端打开给定源，记住结果，并暴露 `IBackendRegistry` 供检查。

## 阅读顺序

如果你要集成该库，请按以下顺序阅读：

1. [Extensions](/zh/api/infrastructure/extensions) — 如何注册。
2. [Core](/zh/api/infrastructure/core) — 你将调用的 `MediaPlayer` 接口面。
3. [Sources](/zh/api/infrastructure/sources) — 如何描述要播放的内容（以及 SSRF 规则）。
4. [Playback](/zh/api/infrastructure/playback) — 回退中间件（只读；通常你不会直接构造它）。
5. [Formats](/zh/api/infrastructure/formats) — 容器探测（只读；由中间件使用）。
6. [Consumers](/zh/api/infrastructure/consumers) — 无头 / 服务端处理。

> 此处记录的所有类型均为 `public`。少数辅助类型（`FrameChannel`、`FormatSignature`、`CodecRegistry`、`FormatKey`）为 `internal`/具体类型；仅在它们能解释可观测行为时才予以说明，你绝不应直接引用它们。`Synchronizer` 与 `SyncAction` 是 `LingFan.Media.Core` 中的 `public` 具体类型（无契约接口）——之所以记录，是因为它们解释了丢帧计数与音视频对齐，但通常你不会直接构造它们。
