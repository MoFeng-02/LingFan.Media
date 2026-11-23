---
title: "BackendDescriptor"
type: "record"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/BackendDescriptor.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["BackendDescriptor", "Name", "Demuxer", "VideoDecoder", "AudioDecoder", "SubtitleDecoder"]
lang: "zh-CN"
---
# BackendDescriptor

源码: [`BackendDescriptor.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/BackendDescriptor.cs)

一个已注册后端的只读描述（工厂接口集合，非实例）。
由 `IBackendRegistry` 按 DI 注册顺序聚合，供中间件 / 高级消费方查看与选定后端。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed record BackendDescriptor(
    string Name,
    IMediaDemuxerFactory Demuxer,
    IVideoDecoderFactory VideoDecoder,
    IAudioDecoderFactory AudioDecoder,
    ISubtitleDecoderFactory? SubtitleDecoder);
```

**参数**

- `Name`：后端友好名（如 "FFmpeg" / "MediaFoundation" / "VLC"）。
- `Demuxer`：解封装器工厂接口。
- `VideoDecoder`：视频解码器工厂接口。
- `AudioDecoder`：音频解码器工厂接口。
- `SubtitleDecoder`：字幕解码器工厂接口（可选；仅部分后端注册）。

## 备注

持有的是 **工厂接口**（DI 解析的 Singleton 无状态服务），**不是** player / 后端实例。

命中某个后端组后，应把这些工厂接口交给
`Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)`
去创建 Session 级对象。**lookup（接口）与 instance（实例）不可混淆。**
