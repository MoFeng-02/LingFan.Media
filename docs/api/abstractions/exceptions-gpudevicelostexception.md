---
title: "GpuDeviceLostException"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Exceptions/GpuDeviceLostException.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GpuDeviceLostException"]
lang: "en-US"
---
# GpuDeviceLostException

Source: [`GpuDeviceLostException.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Exceptions/GpuDeviceLostException.cs)

GPU device missing abnormalities (D3D11 `DXGI_ERROR_DEVICE_REMOVED`/`DEVICE_RESET`,
Vulkan `VK_ERROR_DEVICE_LOST`, etc.).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class GpuDeviceLostException : Exception
```

## Remarks

**Causes**: Device loss caused by driver crash/reset (TDR), GPU removal (external graphics card removed),
Driver upgrades and similar issues cause permanent failure of the device and all its resources (textures/buffers/swap chains), causing any subsequent calls to fail.

**Semantics**: The renderer throws this exception when it detects a device loss (replacing the generalized `InvalidOperationException`),
This allows the upper layer to distinguish by type between "failures recoverable by device rebuild" and "logical errors."

**Recovery Path**: Exceptions are thrown along the Present/Clear call chain to the session layer; The caller releases the current
Play the session and rebuild (reopen OpenAsync → Attach) — the renderer factory will recreate the device.

**Neutrality**: BCL dependency only, without carrying any graphical API specific types; Diagnostic details (DeviceRemovedReason, etc.)
Carried by message text. AOT compatibility: non-serialized constructs (binary serialization under Native AOT is not supported).

## Members

### Constructor

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

Initialize the instance of the default message.
