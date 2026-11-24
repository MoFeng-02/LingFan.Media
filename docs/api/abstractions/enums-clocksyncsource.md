---
title: "ClockSyncSource"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/ClockSyncSource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ClockSyncSource", "Audio", "Video", "System"]
lang: "en-US"
---
# ClockSyncSource

Source: [`ClockSyncSource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/ClockSyncSource.cs)

Clock synchronization source.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ClockSyncSource : int
```

## Values

| Value | Description |
| --- | --- |
| `Audio` | Audio clock is dominant (IAudioOutput.PlaybackPosition driver). |
| `Video` | Video clock is dominant (driven by VideoFrame.Timestamp). |
| `System` | System clock is dominant (Stopwatch drive, downgraded when silent). |
