---
title: "IAudioOutputFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutputFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioOutputFactory", "Create"]
lang: "en-US"
---
# IAudioOutputFactory

Source: [`IAudioOutputFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutputFactory.cs)

Audio output factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioOutputFactory
```

## Remarks

Singleton factory, statusless. Each time Create() returns a new instance (device handle independent).

## Members

### Create

```csharp
IAudioOutput Create();
```

Create a new IAudioOutput instance.

**Returns**: Newly created audio output instance (not initialized, requires calling `Initialize`).
