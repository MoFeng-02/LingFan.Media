---
title: "ProcessingMode"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/ProcessingMode.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ProcessingMode", "RealTime", "Fastest"]
lang: "en-US"
---
# ProcessingMode

Source: [`ProcessingMode.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/ProcessingMode.cs)

Headless / server-side processing mode. Control audio and video synchronization with real-time music play.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ProcessingMode
```

## Values

| Value | Description |
| --- | --- |
| `RealTime` | Real-time mode (default): The master clock advances at a true rhythm based on the audio, and video frames are judged by the synchronizer Present / Wait / Drop。 Used for headless previews or scenes that need to align with an external clock. |
| `Fastest` | Fastest mode: Disable audio and video synchronization (all video frames are released directly), no real-time throttling for headless audio output (Submit instantly, process as soon as possible). Used for batch processing scenarios such as transcoding and offline ML inference, where the faster is better. |
