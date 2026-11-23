---
title: "IHeadlessFrameConsumer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Consumers/IHeadlessFrameConsumer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IHeadlessFrameConsumer", "Consume"]
lang: "zh-CN"
---
# IHeadlessFrameConsumer

源码: [`IHeadlessFrameConsumer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Consumers/IHeadlessFrameConsumer.cs)

无头帧消费契约（后端数据处理）。`VideoFrame` 经此交给下游计算，不渲染、不显示。
简单场景可直接用 `Action{T}` 订阅 `VideoFrameAvailable`，无需本接口；
本接口用于需要结构化生命周期 / 批量汇聚的场景（配合 `LingFan.Media.Consumers.ProcessingFrameSink`）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IHeadlessFrameConsumer : IDisposable, IAsyncDisposable
```

## 备注

零外部引用：仅依赖契约层中立类型（`VideoFrame`）。

只读借用模型：实现方在 `Consume` 中仅可读、可同步拷贝所需数据，
**不得 Dispose、不得跨线程持有传入的 `VideoFrame`**——帧所有权归管线，回调返回后即释放。

对称生命周期：实现 `IDisposable` + `IAsyncDisposable`。

AOT 兼容：纯接口契约，无反射、无 P/Invoke。

## 成员

### Consume

```csharp
void Consume(VideoFrame frame);
```

消费一帧。视频管线在调用后释放该帧（只读借用）。
若需跨调用持有数据，须在方法内同步拷贝
（CPU: `Data` 的 `ReadOnlySpan{T}` → 自有 buffer；
GPU: `ReadbackToCpu` 取托管快照交下游）。

**参数**

- `frame`：当前帧（只读借用，回调返回即失效）。
