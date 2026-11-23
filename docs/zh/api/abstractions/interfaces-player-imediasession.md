---
title: "IMediaSession"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaSession.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaSession", "Source", "Metadata", "VideoTracks", "AudioTracks", "SubtitleTracks", "SelectedVideoTrack", "SelectedAudioTrack", "SelectedSubtitleTrack", "Duration", "IsLive", "CloseAsync"]
lang: "zh-CN"
---
# IMediaSession

源码: [`IMediaSession.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaSession.cs)

播放会话接口。持有打开媒体后的所有信息：轨道、元数据、轨道选择。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaSession
```

## 备注

线程安全：Tracks 等属性在 Open 后只读，可跨线程读取。

## 成员

### Source

```csharp
IMediaSource Source { get; }
```

原始媒体源。

### Metadata

```csharp
MediaMetadata Metadata { get; }
```

媒体元数据。

### VideoTracks

```csharp
IReadOnlyList<MediaTrack> VideoTracks { get; }
```

所有视频轨道。

### AudioTracks

```csharp
IReadOnlyList<MediaTrack> AudioTracks { get; }
```

所有音频轨道。

### SubtitleTracks

```csharp
IReadOnlyList<MediaTrack> SubtitleTracks { get; }
```

所有字幕轨道。

### SelectedVideoTrack

```csharp
MediaTrack? SelectedVideoTrack { get; set; }
```

当前选中视频轨道。

### SelectedAudioTrack

```csharp
MediaTrack? SelectedAudioTrack { get; set; }
```

当前选中音频轨道。

### SelectedSubtitleTrack

```csharp
MediaTrack? SelectedSubtitleTrack { get; set; }
```

当前选中字幕轨道。

### Duration

```csharp
TimeSpan Duration { get; }
```

总时长。

### IsLive

```csharp
bool IsLive { get; }
```

是否直播流。

### CloseAsync

```csharp
Task CloseAsync(CancellationToken ct = default);
```

关闭会话，释放资源。实际管线资源释放由 MediaPlayer.DisposeAsync 负责。
