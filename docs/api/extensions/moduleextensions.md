---
title: "ModuleExtensions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/ModuleExtensions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ModuleExtensions"]
lang: "en-US"
---
# ModuleExtensions

Source: [`ModuleExtensions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/ModuleExtensions.cs)

Auxiliary extension methods for service registration within each submodule.

## Declaration

```csharp
namespace LingFan.Media.Extensions;

public static class ModuleExtensions
```

## Remarks

Service registration for each module is completed directly in their respective extension methods (for example,
`AddFFmpeg()` Register the decoder factory in `FFmpegExtensions.cs`).

This class is currently empty: internal services across modules have been registered at their respective physical locations—
The codec registry (ICodecRegistry) is registered in `AddLingFanMedia()`,
The GPU device context (IGpuDeviceContext) is registered by each renderer extension (such as `AddD3D11Renderer()`).

All methods are synchronous configurations, with no I/O or asynchronous.
