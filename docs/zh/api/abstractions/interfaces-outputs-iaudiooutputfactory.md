---
title: "IAudioOutputFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutputFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioOutputFactory", "Create"]
lang: "zh-CN"
---
# IAudioOutputFactory

源码: [`IAudioOutputFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutputFactory.cs)

音频输出工厂接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioOutputFactory
```

## 备注

Singleton 工厂，无状态。每次 Create() 返回新实例（设备句柄独立）。

## 成员

### Create

```csharp
IAudioOutput Create();
```

创建新的 IAudioOutput 实例。
