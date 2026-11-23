---
title: "SharedGpuSourcePolicy"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSourcePolicy.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SharedGpuSourcePolicy"]
lang: "zh-CN"
---
# SharedGpuSourcePolicy

源码: [`SharedGpuSourcePolicy.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSourcePolicy.cs)

共享表面源选型策略（宿主层设定，模块层只消费）：当同一环境存在多个可用的共享表面源时
（如 Android 上 Vulkan 源与 OpenGL 源并存），由**宿主**按其渲染后端声明优先的句柄形态，
各工厂据此自报 `IsAvailable`。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public static class SharedGpuSourcePolicy
```

## 备注

**为什么需要**：源的选择发生在挂载期，而宿主渲染后端（Vulkan / OpenGL ES / 软件）
只有宿主自己知道；渲染期才发现不匹配会形成「不渲染 ⇒ 不失败 ⇒ 不回退」的死锁——
因此把选型决策前置到宿主，是对该死锁的结构性规避。

**默认值**：`null` = 不干预（各工厂按平台默认自报）。

## 成员

### PreferredKind

```csharp
public static SharedGpuHandleKind? PreferredKind;
```

宿主期望优先的共享表面句柄形态；`null` 表示不干预（按平台默认）。

跨线程读写经 `Volatile`（宿主启动期写入一次，渲染/管线线程读取）。
