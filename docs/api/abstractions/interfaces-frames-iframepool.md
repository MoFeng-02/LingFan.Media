---
title: "IFramePool"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePool.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFramePool", "Rent", "Return"]
lang: "en-US"
---
# IFramePool

Source: [`IFramePool.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IFramePool.cs)

帧对象池接口。供解码器等组件复用帧实例，降低分配与 GC 压力。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFramePool<T> where T : class
```

## Remarks

**线程**：`Rent` 与 `Return` 必须可从任意线程调用——租借与归还可能
分别发生在解码线程、管线线程与 UI 线程，实现须自带线程安全（如并发栈），不得要求调用方加锁。

**所有权**：`Rent` 之后帧归调用方，`Return` 之后归池。
调用方不得在归还后继续访问该帧（池会重置其内容，持有 GPU/缓冲资源的帧会被释放）。

**生命周期**：本接口自身不承担池的释放——池是否实现 `IDisposable`、
释放后两个方法如何表现，由实现声明。约定下限：释放后 `Return` 必须**直接释放**
传入的帧而非入池；`Rent` 允许返回新实例（不要求抛异常），使迟到的归还退化为释放而不是泄漏。

## Members

### Rent

```csharp
T Rent();
```

租用一个帧实例。

### Return

```csharp
void Return(T frame);
```

归还帧实例到池中。
