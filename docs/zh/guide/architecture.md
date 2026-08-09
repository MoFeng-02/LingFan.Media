# 架构

LingFan.Media 组织为一个位于一组**基础设施模块**之上的**契约层**（`Abstractions`），其上还叠加了可插拔的 **后端**、**渲染器**、**输出** 与 **UI** 层。其决定性原则是：*契约层是根基；其余一切都要适应它。*

## 依赖反转就是全部要点

`Abstractions` 只包含签名、自动属性与纯数据模型。它不依赖 BCL 与 `Microsoft.Extensions.Logging.Abstractions`、`Microsoft.Extensions.DependencyInjection.Abstractions` 之外的**任何东西**。正因为如此：

- 后端、渲染器、输出、平台 与 UI 可以在不触及契约的情况下被添加、移除或重写。
- 契约层被允许*增长*（可以添加新的方法签名），但它必须**永远不引用某个具体的 后端 类型**——那将破坏依赖反转的分层。

> **启发式原则：** 如果一个类型被两个或更多层引用，它就属于 `Abstractions`。如果只有一个模块使用它，它就留在那个模块中。

## 12 个逻辑模块

| # | Module | Responsibility |
|---|--------|----------------|
| 01 | `Abstractions` | 跨层契约：接口、模型、枚举、事件（零实现） |
| 02 | `Core` | `MediaPlayer`、`MediaSession`、`VideoPipeline`、`AudioPipeline`、`MediaClock`、`Synchronizer`、`BufferManager` |
| 03 | `Sources` | `FileMediaSource` / `NetworkMediaSource` / `StreamMediaSource` + `MediaStreamFactory` |
| 04 | `Formats` | `FormatDetector`、`DemuxerFactory`、metadata extraction |
| 05 | `Video` | 视频 track、processor chain、deinterlace/scale/color、stats |
| 06 | `Audio` | 音频 track、mixer、volume、effects chain、stats |
| 07 | `Backends` | `FFmpeg` / `VLC` / `MediaFoundation`（实现）；`WebRTC`（stub） |
| 08 | `Renderers` | `D3D11`（实现）；`Vulkan` / `Metal` / `OpenGL`（stubs/partial） |
| 09 | `Outputs` | `WASAPI`、`OpenAL`、`OpenSL ES`、`AAudio`、… |
| 10 | `Platforms` | 平台能力探测与 interop |
| 11 | `Avalonia` | `VideoView`、`MediaControl`、Skia / Composition presenters |
| 12 | `Extensions` | `AddLingFanMedia()`、`MediaBuilder`、codec registry、后端 auto-selection |

## 帧路由 —— 一条路径，多种 Sink

一个视频帧离开 管线 所走的路径恰好只有**一条**：

```
VideoPipeline → _videoFrameSink(frame) → _frameChannel.Emit(frame) → every subscribed IFrameSink
```

`MediaPlayer` 向管线注入一个单一的 sink 委托：`frame => _frameChannel.Emit(frame)`。管线永远不会根据后端或渲染器而分支。`FrameChannel.Emit` 扇出给所有已订阅的 `IFrameSink`。一个有界面的渲染器（`Composition` / `Skia` / `D3D11`）与一个无界面的消费者（`ProcessingFrameSink`）实现*同一个* `IFrameSink` 契约，并从同一通道取用——它们仅在终止动作（present 还是喂给某个算法）与能力（能否消费 GPU 纹理帧）上有所不同。

> **零拷贝是一种 Sink 能力，而非一条独立的分支。** 一个帧是否以零拷贝方式呈现，取决于 Sink 能做什么，而非取决于产生它的代码走的是哪个分支。

## 统一的输出端口

生产与消费被刻意解耦。"做改动"只会在*统一的端口内部*发生。

- **视频输出端口 = `IFrameChannel` + `IFrameSink`。** 全部三种解码 后端 都把帧产出到这一个通道。新的终止能力（recorder Sink、thumbnail Sink、transform chain）通过订阅一个新的 `IFrameSink` 来添加——无需改动 后端 或 管线。
- **音频输出端口 = `IAudioOutput`。** `AudioPipeline` 把每个 后端 解码出的音频归一化进 `IAudioOutput.Submit`。音频被**直接提交给 `IAudioOutput` 并绕过同步器**——音频/视频的不对称是有意为之；我们不会仅仅为了对称就给音频加一条同步分支。无界面的消费者（`ProcessingAudioSink`）与 `WASAPIOutput` 共享 `IAudioOutput` 契约。

## 无界面优先

有界面的路径在字面上就是*无界面 管线 加上一个订阅的 Present Sink*：

```
IVideoRenderer.Present  ←  VideoView.PresentFrame  ←  (subscribed to IFrameChannel)
```

`VideoView` 订阅帧通道并将其桥接到 `IVideoRenderer.Present`。契约保持中立；只有终止 Sink 不同。

## 会话隔离与 DI 分层

- **系统级工厂是 `Singleton`。** `IMediaStreamFactory`、`IFormatDetector`、`ICodecRegistry`、各 后端 工厂，以及 `BackendFallbackMediaPlayerFactory` 都存活于进程生命周期内。
- **每次播放的状态是 `Transient`（一个 Session）。** `MediaPlayer` 在 `OpenAsync` 内部创建它的 `MediaSession`、`MediaClock`、`BufferManager` 与 管线。每个 `IMediaPlayer` 都拥有一个独立的会话；拆除其中一个永远不会干扰另一个。

> 公共门面是 `MediaPlayer`（位于 `Core`）以及 `FallbackMediaPlayer` / `BackendFallbackMediaPlayerFactory`（位于 `Playback`）。不存在单一的 `MediaGraph` 类型——播放由这些门面加上 DI 容器组合而成。
