# 源与解复用

## IFormatDetector

命名空间：`LingFan.Media.Abstractions`

格式检测的依赖倒置点。在 `LingFan.Media.Formats` 中实现；由 `Playback` 中间件消费，该中间件仅依赖此契约（绝不依赖具体类型）。

```csharp
public interface IFormatDetector
{
    MediaFormatProfile DetectProfile(IMediaStream stream);
    Task<MediaFormatProfile> DetectProfileAsync(IMediaStream stream, CancellationToken ct = default);
}
```

只读流的头部（魔数 / 编解码器标签）——无需完整会话。返回一个 `MediaFormatProfile`（容器、主视频编解码器）。对于不可寻址的流，返回 `Unknown` / `Unknown`。由回退调度器用于命中**格式级记忆**并跳过已知不良的后端。

## IMediaDemuxer

命名空间：`LingFan.Media.Abstractions`

将一个容器（MP4 / MKV / …）拆分为按轨道划分的包。

```csharp
public interface IMediaDemuxer : IMediaComponent
{
    Task OpenAsync(IMediaStream stream, CancellationToken ct = default);
    IReadOnlyList<MediaTrack> Tracks { get; }
    MediaMetadata Metadata { get; }
    ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default);
    Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);
    void Close();
}
```

| 成员 | 说明 |
|--------|-------|
| `OpenAsync` | 探测格式并解析轨道。 |
| `Tracks` / `Metadata` | 解析出的轨道 / 容器元数据（打开后跨线程读取安全）。 |
| `ReadPacketAsync` | 下一个包；`null` = 流结束。`ValueTask` 热路径。 |
| `SeekAsync` | 跳转；返回是否成功。 |
| `Close` | 关闭解复用器。 |

> `ReadPacketAsync` / `SeekAsync` 不可并发调用。

## IMediaDemuxerFactory

命名空间：`LingFan.Media.Abstractions`

```csharp
IMediaDemuxer Create(IMediaStream stream);
Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default);
```

## IMediaSource

命名空间：`LingFan.Media.Abstractions`

描述要播放的*对象*。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Type` | `MediaSourceType` | `File` / `Network` / `Stream`。 |
| `Identifier` | `string` | 源标识符（路径 / URL）。 |
| `ConnectAsync(ct)` | `Task` | 打开底层流。 |
| `Read` / `ReadAsync` | 方法 | 读取字节。 |
| `Length` / `Position` | `long` | 流长度 / 位置。 |
| `CanSeek` / `Seek` | bool / 方法 | 可寻址性。 |
| `Close()` | `void` | 关闭。 |

`FileMediaSource` / `NetworkMediaSource` / `StreamMediaSource` 实现了它。

## IMediaStream 与 IMediaStreamFactory

| 类型 | 作用 |
|------|------|
| `IMediaStream` | 一个可读字节流：`Read` / `ReadAsync` / `ConnectAsync` / `Length` / `Position` / `CanSeek` / `Seek` / `Close`。 |
| `IMediaStreamFactory` | 从 `IMediaSource` 创建 `IMediaStream`（Singleton）。 |
