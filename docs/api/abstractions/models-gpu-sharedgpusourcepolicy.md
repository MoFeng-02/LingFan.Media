---
title: "SharedGpuSourcePolicy"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSourcePolicy.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SharedGpuSourcePolicy"]
lang: "en-US"
---
# SharedGpuSourcePolicy

Source: [`SharedGpuSourcePolicy.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSourcePolicy.cs)

Shared surface source selection strategy (host layer setting, module layer consumption only): When multiple available shared surface sources exist in the same environment
(For example, on Android, Vulkan sources coexist with OpenGL sources), the **host** declares the preferred handle form according to its rendering backend,
Each factory reported `IsAvailable` accordingly.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public static class SharedGpuSourcePolicy
```

## Remarks

**Why is it needed**: Source selection occurs during mounting, while host renders backend (Vulkan / OpenGL ES / software)
Only the host himself knows; During rendering, I discovered that mismatches would create deadlocks of 'no rendering⇒ no failure, ⇒ no rollback'—
Therefore, pre-positioning selection decisions to the host is a structural avoidance of deadlocks.

**Default value**: `null` = No intervention (each factory reports by default according to the platform).

## Members

### PreferredKind

```csharp
public static SharedGpuHandleKind? PreferredKind;
```

Shared surface handle form preferred by the host; `null` means no intervention (platform default).

Cross-thread read/write scripture `Volatile` (write once during host startup, read by rendering/pipeline threads).
