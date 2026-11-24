---
title: "IMediaSession"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaSession.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaSession", "Source", "Metadata", "VideoTracks", "AudioTracks", "SubtitleTracks", "SelectedVideoTrack", "SelectedAudioTrack", "SelectedSubtitleTrack", "Duration", "IsLive", "CloseAsync"]
lang: "en-US"
---
# IMediaSession

Source: [`IMediaSession.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaSession.cs)

Play session interface. Holds all information after opening media: tracks, metadata, track selection.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaSession
```

## Remarks

Thread safety: Properties like Tracks are read-only after Open, allowing cross-thread reading.

## Members

### Source

```csharp
IMediaSource Source { get; }
```

Original media source.

### Metadata

```csharp
MediaMetadata Metadata { get; }
```

Media metadata.

### VideoTracks

```csharp
IReadOnlyList<MediaTrack> VideoTracks { get; }
```

All video tracks.

### AudioTracks

```csharp
IReadOnlyList<MediaTrack> AudioTracks { get; }
```

All audio tracks.

### SubtitleTracks

```csharp
IReadOnlyList<MediaTrack> SubtitleTracks { get; }
```

All subtitle tracks.

### SelectedVideoTrack

```csharp
MediaTrack? SelectedVideoTrack { get; set; }
```

Currently select the video track.

### SelectedAudioTrack

```csharp
MediaTrack? SelectedAudioTrack { get; set; }
```

The audio track is currently selected.

### SelectedSubtitleTrack

```csharp
MediaTrack? SelectedSubtitleTrack { get; set; }
```

Currently selected subtitle track.

### Duration

```csharp
TimeSpan Duration { get; }
```

Total duration.

### IsLive

```csharp
bool IsLive { get; }
```

Whether it is a live stream.

### CloseAsync

```csharp
Task CloseAsync(CancellationToken ct = default);
```

Close the session and release resources. Actual pipeline resource release is handled by MediaPlayer.DisposeAsync.

**Parameters**

- `ct`: Cancel token.

**Returns**: Tasks that end when the session is closed complete.
