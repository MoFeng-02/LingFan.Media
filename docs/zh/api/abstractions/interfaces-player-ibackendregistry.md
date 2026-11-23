---
title: "IBackendRegistry"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IBackendRegistry.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IBackendRegistry", "Backends"]
lang: "zh-CN"
---
# IBackendRegistry

源码: [`IBackendRegistry.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IBackendRegistry.cs)

只读后端注册表：聚合所有已注册的后端组（按 DI 注册顺序 = 回退优先级）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IBackendRegistry
```

## 备注

仅暴露工厂接口（`BackendDescriptor`），不暴露具体后端实现，保持依赖倒置。

中间件（如 `LingFan.Media.Playback` 的回退调度器）实现此接口，并据此做运行时单次判断回退。

## 成员

### Backends

```csharp
IReadOnlyList<BackendDescriptor> Backends { get; }
```

所有已注册的后端组，按 DI 注册顺序（即回退优先级，先注册先试）。
