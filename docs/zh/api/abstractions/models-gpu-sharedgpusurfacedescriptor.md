---
title: "struct"
type: "record"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSurfaceDescriptor.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["struct", "0"]
lang: "zh-CN"
---
# struct

源码: [`SharedGpuSurfaceDescriptor.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSurfaceDescriptor.cs)

共享 GPU 表面描述符（中立值对象）。描述一块可被宿主合成器导入的跨设备共享纹理。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public readonly record struct SharedGpuSurfaceDescriptor(
    IntPtr Handle,
    SharedGpuHandleKind Kind,
    int Width,
    int Height,
    SharedGpuSurfaceFormat Format,
    ulong Version,
    SharedGpuSyncMode SyncMode,
    ulong MemorySize = 0,
    ulong MemoryOffset = 0,
    IntPtr NativeImage = default,
    IntPtr NativeDeviceMemory = default,
    uint NativeImageLayout = 0,
    uint NativeVkFormat = 0,
    uint NativeQueueFamilyIndex = 0,
    uint NativeImageUsage = 0,
    uint NativeImageTiling = 0,
    int RotationDegrees = 0)
```

## 备注

**纯数据**：不持有所有权，不可释放——底层纹理/内存生命周期归产出它的
`ISharedGpuSurfaceSource` 管理。

**字段语义表（按 `Kind` 分派，唯一约定）**：

```csharp
Kind                              Handle                     NativeImage        NativeDeviceMemory  MemorySize            MemoryOffset
D3D11TextureGlobalSharedHandle    D3D11 全局共享句柄          0                  0                   可为 0（不做校验）      0
D3D11TextureNtHandle              D3D11 NT 句柄（暂无生产者）  0                  0                   可为 0                 0
VulkanOpaqueNtHandle              外部内存 Win32 句柄         0                  0                   必填（严格相等校验）    0
VulkanOpaquePosixFileDescriptor   外部内存 fd                 0                  0                   必填（严格相等校验）    0
IOSurfaceRef                      IOSurfaceRef 引用           0                  0                   按导入路径需要时填写    0
AndroidHardwareBuffer             AHB 引用（溯源/诊断）        0                  0                   分配字节数             0
VulkanNativeImage                 VkImage（同 NativeImage）    VkImage            VkDeviceMemory      分配字节数             0
GlTexture                         源 AHB 句柄（溯源/诊断）     EGLImageKHR        0                   0                     0
```

**填表规则**：表中未列出的原生字段必须保持 0——它们对该 Kind 无意义，
非 0 会让消费方（或 `HasNativeImage` 这类守卫）误判路径可用。
同一字段在不同 Kind 下含义不同，消费方必须先看 `Kind` 再解引用句柄。

**句柄存活期**：句柄只在「生产者持有」的这段时间内有效——表内的原生句柄
（尤其是 `VulkanOpaquePosixFileDescriptor` 的 fd 与
`IOSurfaceRef` 的引用）由生产者持有至下一次
`TryWriteFrame` 或该源 `Dispose` 为止。
消费方若需跨调用持有，必须自行 dup / CFRetain 并承担对应释放责任；只在本帧内使用则无需复制。

**AOT 兼容**：readonly record struct，无反射、无装箱路径。

## 成员

### 0

```csharp
public bool IsValid => Handle != IntPtr.Zero && Width > 0 && Height > 0;
```

句柄是否有效（非空且尺寸为正）。

**校验范围**：只校验 `Handle` 非空与宽高为正，**不**按 `Kind`
校验该类型必填的原生字段是否齐备。

**按 Kind 的完整判据**见 `SharedGpuSurfaceDescriptor` 的字段语义表：
消费方在真正导入前必须自行对照该表检查本 Kind 的必填字段（如
`VulkanNativeImage` 须同时具备 NativeImage / NativeDeviceMemory /
NativeVkFormat / NativeImageLayout / 队列族与 MemorySize；`GlTexture`
须具备 NativeImage 与 NativeVkFormat）。只凭本属性判定会放过「句柄非空但原生参数不全」的描述符。

### 0

```csharp
public bool HasNativeImage =>
    NativeImage != IntPtr.Zero
    && NativeDeviceMemory != IntPtr.Zero
    && NativeVkFormat != 0
    && NativeImageLayout != 0;
```

是否携带同 device 直采样所需的全套原生参数（VkImage + VkDeviceMemory + 格式 + 布局）。

**用途**：供消费方在导入前做守卫——为 `false` 表示描述符不足以走
`VulkanNativeImage` 直采样路径，应回退到导出/导入路径。

**判据只看上述四个字段、不看 `Kind`**：按字段语义表，非
`VulkanNativeImage` 的 Kind 不应填这些字段，故结果自然为
`false`。
