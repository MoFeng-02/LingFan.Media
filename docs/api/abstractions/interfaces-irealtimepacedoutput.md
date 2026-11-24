---
title: "IRealtimePacedOutput"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/IRealtimePacedOutput.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IRealtimePacedOutput", "PaceRealTime"]
lang: "en-US"
---
# IRealtimePacedOutput

Source: [`IRealtimePacedOutput.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/IRealtimePacedOutput.cs)

Audio output marking interface capable of real-time throttling. Headless (hardwareless) audio output is implemented,
Disable real-time throttling at `Fastest` `IMediaPlayer`
(Submit instantly, process as soon as possible).
Real hardware output (such as WASAPI) is throttled by hardware tempo and does not require implementing this interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IRealtimePacedOutput
```

## Remarks

Zero external references: relies only on contract-layer neutral types.

## Members

### PaceRealTime

```csharp
bool PaceRealTime { set; }
```

Whether to throttle submission at a true pace: true = real-time (default); false = fastest without throttling.
