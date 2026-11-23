---
title: "ModuleExtensions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/ModuleExtensions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ModuleExtensions"]
lang: "zh-CN"
---
# ModuleExtensions

源码: [`ModuleExtensions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/ModuleExtensions.cs)

各子模块内部服务注册的辅助扩展方法。

## 声明

```csharp
namespace LingFan.Media.Extensions;

public static class ModuleExtensions
```

## 备注

各模块的服务注册直接在各自的扩展方法中完成（如
`AddFFmpeg()` 在 `FFmpegExtensions.cs` 中注册解码器工厂）。

本类当前为空占位：跨模块内部服务已在各自实际位置注册——
编解码注册表（ICodecRegistry）在 `AddLingFanMedia()` 中注册，
GPU 设备上下文（IGpuDeviceContext）由各渲染器扩展（如 `AddD3D11Renderer()`）注册。

所有方法为同步配置，无 I/O、无异步。
