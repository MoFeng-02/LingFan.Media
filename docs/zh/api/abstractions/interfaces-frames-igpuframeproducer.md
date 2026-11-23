---
title: "IGpuFrameProducer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuFrameProducer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuFrameProducer", "ApiType", "TryImport", "IsImportSupported", "null"]
lang: "zh-CN"
---
# IGpuFrameProducer

源码: [`IGpuFrameProducer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuFrameProducer.cs)

中立 GPU 帧生产者桥：由渲染器程序集实现并注册为 Singleton，解码后端仅依赖此抽象。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuFrameProducer
```

## 备注

解码器把其原生解码输出（D3D11 共享句柄 / Linux dma_buf / AHardwareBuffer / IOSurface）
经本桥导入为当前渲染器 GPU-API 的纹理（`IGpuTextureResource`），实现零拷贝上屏。
与 `IGpuDeviceContext` 同为 Abstractions 中立桥，严守依赖倒置——
解码后端不反向引用渲染器程序集，亦不感知具体 GPU-API 绑定的创建细节（VkImage 创建由生产者完成）。

**能力自报 + 行为副作用双判据（S_OK≠被接受）**：`TryImport` 在扩展不可用、
原生句柄无效或导入失败时返回 `false`，调用方须回落软件解码并计入 CPU 拷贝统计，
绝不报"已就绪"假绿。

**异步策略**：`TryImport` 为同步原生调用——GPU 纹理导入无 I/O await；
实现保持同步，不补 async（补即伪异步）。

## 成员

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

**参数**

- `source`：解码原生输出描述（句柄 / 尺寸 / 格式 / 切片）。
- `texture`：成功时为本渲染器 GPU-API 纹理（&lt;see cref="IGpuTextureResource"/&gt;），调用方取得所有权；失败时 &lt;see langword="null"/&gt;。

**返回**：`true`=零拷贝导入成功；`false`=不可用，调用方回落软解。

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

**参数**

- `kind`：解码原生输出类型。

**返回**：`true`=支持导入该类型（可启用对应零拷贝路径）。

### null

```csharp
byte[]? DecodeAdapterLuidHint => null;
```

解码设备对齐提示（可选）：生产者声明「解码侧共享句柄应产自哪张 GPU」，供解码器在创建
自有解码设备前对齐适配器，使跨 API 导入落在同一 GPU 上。

**为什么需要它**：跨 GPU/跨厂商导入共享句柄会被驱动拒绝。多数渲染器可自由选卡
（如 Vulkan 按 LUID 对齐到解码卡）；但有的渲染上下文被平台钉死在特定 GPU——典型是
Windows WGL：GL 上下文绑定窗口所在显示器的 GPU，渲染器无法搬走。此时对齐方向必须反转：
解码设备向渲染上下文所在 GPU 靠拢。本属性就是该反向对齐的中立通道。

**语义**：返回 8 字节适配器 LUID（小端，与 DXGI_ADAPTER_DESC1.LUID 一致）表示
「建议解码设备落在该适配器」；返回 `null` 表示无对齐要求（解码器沿用自身选卡策略）。
提示是**建议而非强制**：解码器须先验证目标适配器具备所需解码能力，不具备时忽略提示并按自身策略回落。

**异步策略**：同步属性（纯查询或轻量原生调用，无 I/O await）。

默认实现返回 `null`（无对齐要求，与既有生产者行为兼容）。
