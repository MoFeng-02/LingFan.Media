---
title: "IVideoRendererFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRendererFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoRendererFactory", "Create"]
lang: "zh-CN"
---
# IVideoRendererFactory

源码: [`IVideoRendererFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRendererFactory.cs)

视频渲染器工厂接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoRendererFactory
```

## 备注

Singleton 工厂。GPU 设备上下文（IGpuDeviceContext）由工厂持有

（Singleton 共享 GPU Device），但 SwapChain 在 Create() 中独立创建。

## 成员

### Create

```csharp
IVideoRenderer Create();
```

创建新的 IVideoRenderer 实例（共享 GPU Device，独立 SwapChain）。

**返回**：新创建的视频渲染器实例（未绑定渲染目标，须调用 `Attach`）。
