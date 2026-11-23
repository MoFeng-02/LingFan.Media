---
title: "GpuDeviceCapabilities"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/GpuDeviceCapabilities.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["GpuDeviceCapabilities", "DeviceName", "DedicatedVideoMemory", "SharedSystemMemory", "MaxTextureSize", "SupportsComputeShaders", "SupportsHardwareDecode", "FeatureLevel"]
lang: "zh-CN"
---
# GpuDeviceCapabilities

源码: [`GpuDeviceCapabilities.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/GpuDeviceCapabilities.cs)

GPU 设备能力（中立模型，跨层共享）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class GpuDeviceCapabilities
```

## 备注

由 `GetCapabilities` 返回，Avalonia / Outputs 等层可读取设备能力
而无需引用具体渲染器模块（依赖倒置严守）。

纯数据不可变模型，AOT 友好：仅含 BCL 类型与 Abstractions 已有枚举，零外部引用。

## 成员

### 构造函数

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

初始化 `GpuDeviceCapabilities` 的新实例。

**参数**

- `deviceName`：GPU 名称。
- `dedicatedVideoMemory`：专用显存（字节）。
- `sharedSystemMemory`：共享系统内存（字节）。
- `maxTextureSize`：最大纹理尺寸（像素）。
- `supportsComputeShaders`：是否支持计算着色器。
- `supportsHardwareDecode`：是否支持硬件解码（DXVA / VA-API 等）。
- `featureLevel`：D3D Feature Level 原始值（如 0xB000 = 11_0）；非 D3D API 填 -1。

### DeviceName

```csharp
public string DeviceName { get; }
```

GPU 名称。

### DedicatedVideoMemory

```csharp
public ulong DedicatedVideoMemory { get; }
```

专用显存（字节）。

### SharedSystemMemory

```csharp
public ulong SharedSystemMemory { get; }
```

共享系统内存（字节）。

### MaxTextureSize

```csharp
public int MaxTextureSize { get; }
```

最大纹理尺寸（像素）。

### SupportsComputeShaders

```csharp
public bool SupportsComputeShaders { get; }
```

是否支持计算着色器。

### SupportsHardwareDecode

```csharp
public bool SupportsHardwareDecode { get; }
```

是否支持硬件解码（DXVA / VA-API 等）。

### FeatureLevel

```csharp
public int FeatureLevel { get; }
```

D3D Feature Level 原始值（如 0xB000 = 11_0）；非 D3D API 填 -1。
