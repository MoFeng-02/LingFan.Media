---
title: "IGpuFrameProducer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuFrameProducer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuFrameProducer", "ApiType", "TryImport", "IsImportSupported"]
lang: "en-US"
---
# IGpuFrameProducer

Source: [`IGpuFrameProducer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuFrameProducer.cs)

中立 GPU 帧生产者桥：由渲染器程序集实现并注册为 Singleton，解码后端仅依赖此抽象。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuFrameProducer
```

## Remarks

解码器把其原生解码输出（D3D11 共享句柄 / Linux dma_buf / AHardwareBuffer / IOSurface）
经本桥导入为当前渲染器 GPU-API 的纹理（`IGpuTextureResource`），实现零拷贝上屏。
与 `IGpuDeviceContext` 同为 Abstractions 中立桥，严守依赖倒置——
解码后端不反向引用渲染器程序集，亦不感知具体 GPU-API 绑定的创建细节（VkImage 创建由生产者完成）。

**能力自报 + 行为副作用双判据（S_OK≠被接受）**：`TryImport` 在扩展不可用、
原生句柄无效或导入失败时返回 `false`，调用方须回落软件解码并计入 CPU 拷贝统计，
绝不报"已就绪"假绿。

**异步策略**：`TryImport` 为同步原生调用——GPU 纹理导入无 I/O await；
实现保持同步，不补 async（补即伪异步）。

## Members

### ApiType

```csharp
GPUApiType ApiType { get; }
```

本生产者面向的 GPU API（须与激活渲染器一致，供解码器按 `ApiType` 匹配）。

### TryImport

```csharp
bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture);
```

尝试将解码原生输出导入为本渲染器 GPU-API 纹理（零拷贝）。

**Parameters**

- `source`: 解码原生输出描述（句柄 / 尺寸 / 格式 / 切片）。
- `texture`: 成功时为本渲染器 GPU-API 纹理（&lt;see cref="IGpuTextureResource"/&gt;），调用方取得所有权；失败时 &lt;see langword="null"/&gt;。

**Returns**: `true`=零拷贝导入成功；`false`=不可用，调用方回落软解。

### IsImportSupported

```csharp
bool IsImportSupported(GpuFrameImportKind kind) => true;
```

预检：本生产者是否支持导入指定类型的解码原生输出（解码器据此决定是否启用对应零拷贝路径）。

**与 `TryImport` 的分工**：本方法是「路径启用前」的能力预检（如 Android 解码器
在 configure 到 ImageReader Surface 前判定 AHB 导入可行性），避免在注定失败的路径上消耗解码器
重建成本；`TryImport` 仍是运行时导入的行为判据（能力自报 + 行为副作用双判据，不假绿）。

默认实现乐观返回 `true`（与既有消费方行为兼容：由 TryImport 运行时兜底）。

**对生产者的要求**：默认 `true` 只是兼容既有实现的过渡口径，**不是**
「全部类型都支持」的授权。新生产者必须按实际支持的 `GpuFrameImportKind` 覆写本方法；
不覆写即视为向解码器承诺「所有类型都能零拷贝导入」，解码器会据此启用注定失败的路径，
直到 TryImport 运行时返回 `false` 才回落——期间浪费的是解码器重建成本。

**Parameters**

- `kind`: 解码原生输出类型。

**Returns**: `true`=支持导入该类型（可启用对应零拷贝路径）。
