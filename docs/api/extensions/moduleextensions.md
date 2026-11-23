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

各子模块内部服务注册的辅助扩展方法。

## Declaration

```csharp
namespace LingFan.Media.Extensions;

public static class ModuleExtensions
```

## Remarks

各模块的服务注册直接在各自的扩展方法中完成（如
`AddFFmpeg()` 在 `FFmpegExtensions.cs` 中注册解码器工厂）。

此保留类用于未来跨模块的内部服务注册，如编解码注册表、GPU 设备上下文等。

所有方法为同步配置（config 分类），无 I/O、无异步。
