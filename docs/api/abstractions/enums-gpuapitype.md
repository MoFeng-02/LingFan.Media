---
title: "GPUApiType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/GPUApiType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GPUApiType", "D3D11", "Vulkan", "Metal", "OpenGL"]
lang: "en-US"
---
# GPUApiType

Source: [`GPUApiType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/GPUApiType.cs)

GPU API 类型。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum GPUApiType : int
```

## Values

| Value | Description |
| --- | --- |
| `D3D11` | Direct3D 11（Windows）。 |
| `Vulkan` | Vulkan（Windows/Linux/Android）。 |
| `Metal` | Metal（macOS/iOS）。 |
| `OpenGL` | OpenGL（桌面兼容备用）。 |
