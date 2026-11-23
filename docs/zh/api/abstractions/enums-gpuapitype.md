---
title: "GPUApiType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/GPUApiType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GPUApiType", "D3D11", "Vulkan", "Metal", "OpenGL"]
lang: "zh-CN"
---
# GPUApiType

源码: [`GPUApiType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/GPUApiType.cs)

GPU API 类型。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum GPUApiType : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `D3D11` | Direct3D 11（Windows）。 |
| `Vulkan` | Vulkan（Windows/Linux/Android）。 |
| `Metal` | Metal（macOS/iOS）。 |
| `OpenGL` | OpenGL（桌面兼容备用）。 |
