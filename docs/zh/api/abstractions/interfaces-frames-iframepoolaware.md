---
title: "IFramePoolAware"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePoolAware.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFramePoolAware", "SetFramePool"]
lang: "zh-CN"
---
# IFramePoolAware

源码: [`IFramePoolAware.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePoolAware.cs)

帧池感知接口。可选实现，支持帧对象池的组件通过此接口接收池引用。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IFramePoolAware<T> where T : class
```

## 备注

解码器等组件可选实现此接口。调用方（如 MediaPlayer）通过 pattern matching 检测并注入池。

不修改现有接口（IMediaComponent/IVideoDecoder 等），通过 pattern matching 注入，保持架构兼容。

**AOT 兼容**：接口 + pattern matching（is 运算符），编译期确定类型，无反射。

**异步策略**：sync（config 分类）——纯内存设置引用，无 I/O。

## 成员

### SetFramePool

```csharp
void SetFramePool(IFramePool<T>? pool);
```

设置帧对象池。传入 null 表示禁用池化，组件将自行创建帧实例。

**参数**

- `pool`：帧对象池（可为 null）。
