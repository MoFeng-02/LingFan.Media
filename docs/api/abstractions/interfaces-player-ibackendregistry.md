---
title: "IBackendRegistry"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IBackendRegistry.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IBackendRegistry", "Backends"]
lang: "en-US"
---
# IBackendRegistry

Source: [`IBackendRegistry.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IBackendRegistry.cs)

Read-only backend registry: aggregates all registered backend groups (DI registration order = rollback priority).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IBackendRegistry
```

## Remarks

Only the factory interface (`BackendDescriptor`) is exposed, without exposing the specific backend implementation, maintaining dependency inversion.

Middleware (such as the rollback scheduler on `LingFan.Media.Playback`) implements this interface and performs a single checkback at runtime based on it.

## Members

### Backends

```csharp
IReadOnlyList<BackendDescriptor> Backends { get; }
```

All registered backend groups are registered in DI order (i.e., rollback priority, first to test first).
