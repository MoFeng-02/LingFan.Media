---
title: "SharedGpuAdapterIdentity"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSyncModel.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SharedGpuAdapterIdentity", "Unknown", "DeviceUuid", "DeviceLuid", "MatchesDeviceUuid", "MatchesDeviceLuid"]
lang: "en-US"
---
# SharedGpuAdapterIdentity

Source: [`SharedGpuSyncModel.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSyncModel.cs)

目标 GPU 适配器身份（中立值对象）。由消费方（宿主合成器）提供，供生产者选择**同一物理设备**。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SharedGpuAdapterIdentity
```

## Remarks

**为什么需要它**：跨设备共享纹理的导入要求生产者与合成器位于同一物理 GPU
（多 GPU 机器上尤为关键）。若生产者自行按偏好评分选设备，可能选中与合成器不同的适配器，
导致导入失败或静默黑屏。消费方把自身适配器身份透传给工厂，生产者据此优选匹配设备，
无法匹配时应抛 `NotSupportedException` 让调用方干净回退。

**字段来源**：`DeviceUuid` 对应 Vulkan
`VkPhysicalDeviceIDProperties.deviceUUID`（16 字节）；
`DeviceLuid` 对应 DXGI 适配器 LUID（8 字节）。
宿主可能只提供其中之一，甚至都不提供（此时生产者按自身偏好选择）。

## Members

### new

```csharp
public static SharedGpuAdapterIdentity Unknown { get; } = new(default, default);
```

身份未知（宿主未提供任何标识）。生产者应按自身偏好选择设备。

### Constructor

```csharp
public SharedGpuAdapterIdentity(ReadOnlyMemory<byte> deviceUuid, ReadOnlyMemory<byte> deviceLuid);
```

初始化 `SharedGpuAdapterIdentity` 的新实例。

**Parameters**

- `deviceUuid`: Vulkan 物理设备 UUID（16 字节）；未知时传 &lt;see langword="default"/&gt;。
- `deviceLuid`: DXGI 适配器 LUID（8 字节）；未知时传 &lt;see langword="default"/&gt;。

### DeviceUuid

```csharp
public ReadOnlyMemory<byte> DeviceUuid { get; }
```

Vulkan 物理设备 UUID（16 字节）。长度不为 16 视为未提供。

### DeviceLuid

```csharp
public ReadOnlyMemory<byte> DeviceLuid { get; }
```

DXGI 适配器 LUID（8 字节）。长度不为 8 视为未提供。

### 16

```csharp
public bool HasDeviceUuid => DeviceUuid.Length == 16;
```

是否提供了可用的 Vulkan 设备 UUID。

### 8

```csharp
public bool HasDeviceLuid => DeviceLuid.Length == 8;
```

是否提供了可用的 DXGI 适配器 LUID。

### HasDeviceLuid

```csharp
public bool IsUnknown => !HasDeviceUuid && !HasDeviceLuid;
```

是否未提供任何标识。

### MatchesDeviceUuid

```csharp
public bool MatchesDeviceUuid(ReadOnlySpan<byte> candidate)
    => HasDeviceUuid && candidate.Length == 16 && DeviceUuid.Span.SequenceEqual(candidate);
```

判断给定的 16 字节 UUID 是否与本身份一致。

**Parameters**

- `candidate`: 候选设备 UUID。

**Returns**: 本身份未提供 UUID 时返回 `false`（调用方应视为「无法确认」而非「不匹配」）。

### MatchesDeviceLuid

```csharp
public bool MatchesDeviceLuid(ReadOnlySpan<byte> candidate)
    => HasDeviceLuid && candidate.Length == 8 && DeviceLuid.Span.SequenceEqual(candidate);
```

判断给定的 8 字节 LUID 是否与本身份一致。

**Parameters**

- `candidate`: 候选适配器 LUID。

**Returns**: 本身份未提供 LUID 时返回 `false`。
