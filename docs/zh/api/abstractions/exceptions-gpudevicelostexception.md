---
title: "GpuDeviceLostException"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Exceptions/GpuDeviceLostException.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GpuDeviceLostException"]
lang: "zh-CN"
---
# GpuDeviceLostException

源码: [`GpuDeviceLostException.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Exceptions/GpuDeviceLostException.cs)

GPU 设备丢失异常（D3D11 `DXGI_ERROR_DEVICE_REMOVED`/`DEVICE_RESET`、
Vulkan `VK_ERROR_DEVICE_LOST` 等）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class GpuDeviceLostException : Exception
```

## 备注

**成因**：设备丢失由驱动崩溃/重置（TDR）、GPU 移除（外接显卡拔出）、
驱动升级等引起——设备及其全部资源（纹理/缓冲/交换链）永久失效，任何后续调用都会失败。

**语义**：渲染器检测到设备丢失时抛出本异常（替代泛化的 `InvalidOperationException`），
使上层能以类型区分「可通过重建设备恢复的故障」与「逻辑错误」。

**恢复路径**：异常沿 Present/Clear 调用链上抛至会话层；调用方释放当前
播放会话并重建（重新 OpenAsync → Attach）——渲染器工厂会重新创建设备。

**中立性**：仅 BCL 依赖，不携带任何图形 API 具体类型；诊断细节（DeviceRemovedReason 等）
以消息文本承载。AOT 兼容：无序列化构造（NativeAOT 下二进制序列化不受支持）。

## 成员

### 构造函数

```csharp
public GpuDeviceLostException()
    : base("GPU 设备已丢失，需释放并重建渲染会话。") { }

/// <summary>以指定消息初始化实例。</summary>
/// <param name="message">异常消息（应包含底层错误码与操作上下文）。</param>
public GpuDeviceLostException(string message) : base(message) { }

/// <summary>以指定消息与内部异常初始化实例。</summary>
/// <param name="message">异常消息。</param>
/// <param name="innerException">底层异常。</param>
public GpuDeviceLostException(string message, Exception innerException)
    : base(message, innerException) { }
```

初始化默认消息的实例。
