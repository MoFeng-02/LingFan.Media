# 契约层（Abstractions）

`LingFan.Media.Abstractions` 项目是**契约层**——系统的法则。它包含**零实现**：只有接口签名、自动属性、纯数据模型、枚举，以及事件参数类型。它不依赖 BCL 与 `Microsoft.Extensions.Logging.Abstractions` 之外的任何内容。

## 这里包含什么

| 类别 | 数量 | 示例 |
|----------|-------|----------|
| 接口 | 43 | `IMediaPlayer`, `IFrameChannel`, `IMediaClock`, `IVideoRenderer`, `IAudioOutput`, `IMediaDemuxer`, `IFormatDetector` |
| 模型 | 23 | `VideoFrame`, `AudioFrame`, `SubtitleFrame`, `MediaPacket`, `MediaTrack`, `MediaFormatProfile` |
| 枚举 | 22 | `MediaState`, `VideoCodec`, `PixelFormat`, `ContainerFormat`, `ClockSyncSource`, `GPUApiType` |
| 事件 | 5 | `MediaStateChangedEventArgs`, `MediaErrorEventArgs`, `BufferProgressEventArgs`, `TrackChangedEventArgs`, `LogEventArgs` |

## 两条原则

1. **零外部引用。** 每个参数与返回类型要么是 BCL 类型（`IDisposable`、`Memory<byte>`、`Stream`、`CancellationToken`、`Task` / `ValueTask`），要么是已在 `Abstractions` 中声明的类型。任何后端、渲染器或 UI 的具体类型都不得出现在契约签名中。
2. **零实现。** 只有签名、自动属性和纯数据模型（包括 `Dispose` 释放该类型自身的中性资源）。不含业务逻辑，不对具体类型做 `new`。

> 由于该层零外部引用，后端 / 渲染器 / 输出 / 平台 / UI 可以在不触及契约的情况下被增删或重写。这正是依赖倒置的真正回报。

## 子页面

- [播放器与会话](/zh/api/abstractions/player) — `IMediaPlayer`、`IMediaSession`、`IMediaPlayerFactory`、`BackendDescriptor`
- [帧与资源](/zh/api/abstractions/frames) — `IFrameChannel`、`IFrameSink`、`VideoFrame`、`AudioFrame`、`MediaPacket`、`IFrameResource`
- [时钟与缓冲](/zh/api/abstractions/clock) — `IMediaClock`、`IBufferManager`、`IMediaComponent`
- [源与解复用](/zh/api/abstractions/demuxer) — `IFormatDetector`、`IMediaDemuxer`、`IMediaSource`、`IMediaStream`
- [解码器与编解码器](/zh/api/abstractions/decoders) — `IVideoDecoder`、`IAudioDecoder`、`ISubtitleDecoder`、`ICodecRegistry`
- [渲染器与输出](/zh/api/abstractions/renderers) — `IVideoRenderer`、`IAudioOutput`、`IAudioEngine`
- [枚举](/zh/api/abstractions/enums) — 全部 22 个枚举
- [事件与异常](/zh/api/abstractions/events-exceptions) — 事件参数与异常类型
