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

Target GPU adapter identity (neutral value object). Provided by the consumer (host compositor) for producers to choose **the same physical device**.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SharedGpuAdapterIdentity
```

## Remarks

**Why You Need It**: Importing textures to share across devices requires producers and synthesizers to be on the same physical GPU
(This is especially critical on multi-GPU machines). If producers select equipment based on their own preference scores, they may choose adapters different from synthesizers,
Causing import failure or silent black screen. The consumer communicates their adapter identity to the factory, and the producer selects the equipment accordingly.
If no match is possible, `NotSupportedException` should be thrown to have the caller revert cleanly.

**Field Source**: `DeviceUuid` Corresponds to Vulkan
`VkPhysicalDeviceIDProperties.deviceUUID` (16 bytes);
`DeviceLuid` Corresponds to DXGI adapter LUID (8 bytes).
The host may only provide one of these or even none (in which case the producer chooses according to their own preference).

## Members

### new

```csharp
public static SharedGpuAdapterIdentity Unknown { get; } = new(default, default);
```

Identity unknown (host did not provide any identifier). Producers should select equipment according to their own preferences.

### Constructor

```csharp
public SharedGpuAdapterIdentity(ReadOnlyMemory<byte> deviceUuid, ReadOnlyMemory<byte> deviceLuid);
```

Initialize a new instance of `SharedGpuAdapterIdentity`.

**Parameters**

- `deviceUuid`: Vulkan physical device UUID (16 bytes); Unknown time-pass &lt;see langword="default"/&gt;.
- `deviceLuid`: DXGI adapter LUID (8 bytes); Unknown time-pass &lt;see langword="default"/&gt;.

### DeviceUuid

```csharp
public ReadOnlyMemory<byte> DeviceUuid { get; }
```

Vulkan physical device UUID (16 bytes). Lengths not 16 are considered unprovided.

### DeviceLuid

```csharp
public ReadOnlyMemory<byte> DeviceLuid { get; }
```

DXGI adapter LUID (8 bytes). Length not 8 is considered not provided.

### 16

```csharp
public bool HasDeviceUuid => DeviceUuid.Length == 16;
```

Whether available Vulkan device UUIDs are provided.

### 8

```csharp
public bool HasDeviceLuid => DeviceLuid.Length == 8;
```

Whether available DXGI adapter LUIDs are provided.

### HasDeviceLuid

```csharp
public bool IsUnknown => !HasDeviceUuid && !HasDeviceLuid;
```

Whether no identification was provided.

### MatchesDeviceUuid

```csharp
public bool MatchesDeviceUuid(ReadOnlySpan<byte> candidate)
    => HasDeviceUuid && candidate.Length == 16 && DeviceUuid.Span.SequenceEqual(candidate);
```

Determine whether the given 16-byte UUID matches the identity.

**Parameters**

- `candidate`: Candidate device UUID.

**Returns**: Returns `false` if the identity does not provide a UUID (caller should consider it "unconfirmed" rather than "mismatch").

### MatchesDeviceLuid

```csharp
public bool MatchesDeviceLuid(ReadOnlySpan<byte> candidate)
    => HasDeviceLuid && candidate.Length == 8 && DeviceLuid.Span.SequenceEqual(candidate);
```

Determine whether the given 8-byte LUID matches the identity.

**Parameters**

- `candidate`: Candidate adapter LUID.

**Returns**: Returns `false` if the identity does not provide a LUID.
