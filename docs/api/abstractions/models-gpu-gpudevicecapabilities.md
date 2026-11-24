---
title: "GpuDeviceCapabilities"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/GpuDeviceCapabilities.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GpuDeviceCapabilities", "DeviceName", "DedicatedVideoMemory", "SharedSystemMemory", "MaxTextureSize", "SupportsComputeShaders", "SupportsHardwareDecode", "FeatureLevel"]
lang: "en-US"
---
# GpuDeviceCapabilities

Source: [`GpuDeviceCapabilities.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/GpuDeviceCapabilities.cs)

GPU device capabilities (neutral model, cross-layer sharing).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class GpuDeviceCapabilities
```

## Remarks

Returned from `GetCapabilities`, Avalonia / Outputs and other layers can read device capabilities
Without referencing specific renderer modules (strict inversion dependent).

Pure data immutable model, AOT-friendly: only includes BCL types and Abstractions, enumerated with zero external references.

## Members

### Constructor

```csharp
public GpuDeviceCapabilities(
    string deviceName,
    ulong dedicatedVideoMemory,
    ulong sharedSystemMemory,
    int maxTextureSize,
    bool supportsComputeShaders,
    bool supportsHardwareDecode,
    int featureLevel);
```

Initialize a new instance of `GpuDeviceCapabilities`.

**Parameters**

- `deviceName`: GPU name.
- `dedicatedVideoMemory`: Dedicated video memory (bytes).
- `sharedSystemMemory`: Shared system memory (bytes).
- `maxTextureSize`: Maximum texture size (pixels).
- `supportsComputeShaders`: Whether computed shaders are supported.
- `supportsHardwareDecode`: Whether hardware decoding is supported (DXVA / VA-API, etc.).
- `featureLevel`: D3D Feature Level raw value (e.g., 0xB000 = 11_0); For non-D3D APIs, enter -1.

### DeviceName

```csharp
public string DeviceName { get; }
```

GPU name.

### DedicatedVideoMemory

```csharp
public ulong DedicatedVideoMemory { get; }
```

Dedicated video memory (bytes).

### SharedSystemMemory

```csharp
public ulong SharedSystemMemory { get; }
```

Shared system memory (bytes).

### MaxTextureSize

```csharp
public int MaxTextureSize { get; }
```

Maximum texture size (pixels).

### SupportsComputeShaders

```csharp
public bool SupportsComputeShaders { get; }
```

Whether computed shaders are supported.

### SupportsHardwareDecode

```csharp
public bool SupportsHardwareDecode { get; }
```

Whether hardware decoding is supported (DXVA / VA-API, etc.).

### FeatureLevel

```csharp
public int FeatureLevel { get; }
```

D3D Feature Level raw value (e.g., 0xB000 = 11_0); For non-D3D APIs, enter -1.
