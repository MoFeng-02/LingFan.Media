---
title: "IGpuDeviceContext"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/IGpuDeviceContext.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuDeviceContext", "ApiType", "DeviceHandle", "ContextHandle", "SharedDevice", "SharedPhysicalDevice", "VideoQueueFamilyIndex", "GraphicsQueueFamilyIndex", "false", "IsInitialized", "InitializeAsync", "GetCapabilities"]
lang: "zh-CN"
---
# IGpuDeviceContext

源码: [`IGpuDeviceContext.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/IGpuDeviceContext.cs)

中立 GPU 设备上下文契约（跨层共享，零外部引用）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuDeviceContext
```

## 备注

作为 Abstractions 中立桥，供 Avalonia / Outputs 等层查询 GPU 能力或获取纹理句柄，
而无需引用具体渲染器模块（如 Renderers.D3D11），严守依赖倒置。

**异步策略**：`InitializeAsync` 为接口契约——当底层共享 GPU 设备已由
工厂（如 `D3D11RendererFactory`）创建、本方法仅做能力查询（同步 COM 调用）**无真实 I/O await** 时，
实现返回 `CompletedTask`（非伪异步，因无隐藏阻塞）。若某后端实现中确需异步初始化，
则一路 `await` 实现真异步。判断口诀：方法体有无真实 await → 有则 async，无则 Task.CompletedTask。

## 成员

### ApiType

```csharp
GPUApiType ApiType { get; }
```

GPU API 类型。

### DeviceHandle

```csharp
IntPtr DeviceHandle { get; }
```

共享 GPU 设备原生句柄（如 ID3D11Device* 的指针）。无设备时为 `Zero`。

**指针语义由 `ApiType` 决定**：本属性与 `ContextHandle` 只承诺
「原生句柄的整数形态」，不承诺它指向哪种对象——消费方必须先按 `ApiType` 分派再解引用，
不得按某个后端的形态硬解。

### ContextHandle

```csharp
IntPtr ContextHandle { get; }
```

共享 GPU 设备上下文原生句柄（如 ID3D11DeviceContext* 指针）。
用于硬件解码器初始化（D3D11VA 需要 device + device_context）。无上下文时为 `Zero`。

**指针语义由 `ApiType` 决定**：D3D11 下是 device context；OpenGL 族后端下
实际承载的是 `EGLDisplay`（GL 没有与 device context 对应的独立对象）。消费方须按
`ApiType` 分派后使用，不得跨后端套用同一解释。

### SharedDevice

```csharp
object? SharedDevice { get; }
```

共享 GPU 设备对象（运行时显式 cast，如 ID3D11Device / Device(Vulkan)）。
供解码器复用渲染器共享设备进行零拷贝；无设备时为 `null`。

### SharedPhysicalDevice

```csharp
object? SharedPhysicalDevice { get; }
```

共享 GPU 物理设备对象（Vulkan 等需「同物理设备对齐」零拷贝时提供，盒装 PhysicalDevice）。
非 Vulkan 后端为 `null`。

### VideoQueueFamilyIndex

```csharp
uint VideoQueueFamilyIndex { get; }
```

视频解码队列族索引（对应 `VK_QUEUE_VIDEO_DECODE_BIT_KHR`）。
设备创建时启用了 video-decode 扩展且存在该队列族才非 `MaxValue`；否则为 `MaxValue`（解码器回落软件解码）。

### GraphicsQueueFamilyIndex

```csharp
uint GraphicsQueueFamilyIndex { get; }
```

图形渲染队列族索引（对应 `VK_QUEUE_GRAPHICS_BIT`）。
Vulkan 硬解 DPB 图像需在「video-decode 队列族」与「图形队列族」之间共享（跨队列族读写零拷贝上屏），
此值供解码器在创建 DPB 图像时正确设置 `VK_SHARING_MODE_CONCURRENT` 的队列族列表。
仅 Vulkan 后端有意义；非 Vulkan 后端或单一队列族时为 `MaxValue`（解码器退化为同族独占）。

### false

```csharp
bool SupportsVideoProfileIndependent => false;
```

设备是否已启用 video-maintenance1 能力（Vulkan `VK_KHR_video_maintenance1` 的
`videoMaintenance1` 特性）。启用后 DPB 图像可带
`VK_IMAGE_CREATE_VIDEO_PROFILE_INDEPENDENT_BIT_KHR`，绕过「flags ⊆ 驱动格式支持集」
检查（VUID-06811），使 per-plane 视图所需的 MUTABLE_FORMAT 在支持集 flags=0 的驱动上仍合法。
非 Vulkan 后端恒 `false`。默认 false（DIM 兼容既有实现）。

### IsInitialized

```csharp
bool IsInitialized { get; }
```

设备是否已初始化（共享设备已创建）。

### InitializeAsync

```csharp
Task InitializeAsync(CancellationToken ct = default);
```

确保 GPU 设备上下文就绪（绑定设备 / 查询能力）。
接口契约：仅做同步 COM 能力查询时返回 `CompletedTask`（非伪异步）。

**参数**

- `ct`：取消令牌。

**返回**：设备就绪的任务（实现无 I/O 时即 `CompletedTask`）。

### GetCapabilities

```csharp
GpuDeviceCapabilities GetCapabilities();
```

获取 GPU 设备能力（纯内存查询，同步消费）。

**返回**：设备能力快照。
