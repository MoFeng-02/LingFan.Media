---
title: "MediaState"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaState.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaState", "Idle", "Opening", "Buffering", "Playing", "Paused", "Stopped", "Ended", "Error"]
lang: "en-US"
---
# MediaState

Source: [`MediaState.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaState.cs)

Media playback status.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum MediaState : int
```

## Values

| Value | Description |
| --- | --- |
| `Idle` | Idle, media source not yet open. |
| `Opening` | Opening the media source. |
| `Buffering` | Buffering, waiting for data to be ready. |
| `Playing` | Currently playing. |
| `Paused` | Paused. |
| `Stopped` | Stopped, can be restarted. |
| `Ended` | Automatic playback ends. |
| `Error` | Error status. |
