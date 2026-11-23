---
title: "ISharedGpuSurfaceSourceFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/ISharedGpuSurfaceSource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ISharedGpuSurfaceSourceFactory", "HandleKind", "IsAvailable", "Create"]
lang: "en-US"
---
# ISharedGpuSurfaceSourceFactory

Source: [`ISharedGpuSurfaceSource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/ISharedGpuSurfaceSource.cs)

`ISharedGpuSurfaceSource` 的工厂（中立契约）。经 DI 注册多个实现，
由消费方按「宿主合成器支持的句柄类型」挑选可用者。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface ISharedGpuSurfaceSourceFactory
```

## Remarks

**注册即插拔**：每个 GPU 后端注册一个工厂；UI 层遍历
`IEnumerable<ISharedGpuSurfaceSourceFactory>`，选中第一个
`IsAvailable` 且句柄类型被宿主合成器支持的工厂。
因此 UI 层不含任何「优先 D3D11 / 其次 Vulkan」的硬编码分支。

**开箱即用原则**：`IsAvailable` 应为轻量判定（平台/DI 可用性），
**不得**在此触碰原生资源；真正的设备/纹理创建延迟到 `Create`。

## Members

### HandleKind

```csharp
SharedGpuHandleKind HandleKind { get; }
```

本工厂产出的共享句柄类型（不创建实例即可查询）。

### IsAvailable

```csharp
bool IsAvailable { get; }
```

当前环境下本适配器是否可用（平台匹配、依赖就绪）。轻量判定，不触碰原生资源。

### Create

```csharp
ISharedGpuSurfaceSource Create(SharedGpuAdapterIdentity? targetAdapter = null);
```

创建共享表面源实例。由调用方负责释放。

**Parameters**

- `targetAdapter`: 消费方（宿主合成器）所在的物理 GPU 身份（可选）。生产者据此优选&lt;b&gt;同一物理设备&lt;/b&gt;，
无法匹配时应抛 &lt;see cref="NotSupportedException"/&gt; 让调用方干净回退到下一个工厂。
为空表示消费方未提供身份，生产者按自身偏好选择。

**Returns**: 共享表面源。
