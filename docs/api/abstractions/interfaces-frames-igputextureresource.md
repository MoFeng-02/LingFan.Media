---
title: "IGpuTextureResource"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuTextureResource.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuTextureResource", "NativeTextureHandle", "SubresourceIndex", "ReadbackToCpu"]
lang: "en-US"
---
# IGpuTextureResource

Source: [`IGpuTextureResource.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuTextureResource.cs)

中立 GPU 纹理帧资源契约（跨层共享，零外部引用）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuTextureResource : IFrameResource
```

## Remarks

由具体渲染器/后端实现（如 `LingFan.Media.Renderers.Vulkan.VulkanImageResource`），
供 Avalonia / Outputs 等层以平台无关方式消费 GPU 纹理（例如 CPU 回读后送入 Skia WriteableBitmap 回退路径），
而无需引用具体渲染器模块，严守依赖倒置。与 `IGpuDeviceContext` 同为 Abstractions 中立桥。

**异步策略**：`ReadbackToCpu` 为同步 COM/原生调用——D3D11/GL 纹理回读
无真实 I/O await。实现保持同步，不补 async（补即伪异步）。

## Members

### NativeTextureHandle

```csharp
IntPtr NativeTextureHandle { get; }
```

原生 GPU 纹理句柄（如 ID3D11Texture2D* 的 COM 指针）。
供渲染器直接 GPU 拷贝（零拷贝路径），无需 CPU 回读。

调用方负责确保使用期间资源不被释放（如渲染器持锁或帧引用计数）。
句柄所有权归 `IFrameResource` 实现的 `Dispose` 管理。

### SubresourceIndex

```csharp
int SubresourceIndex { get; }
```

子资源索引（纹理数组切片索引，非数组时为 0）。
DXVA 硬解输出纹理数组时，需用此索引通过 CopySubresourceRegion 拷贝正确的切片。

### ReadbackToCpu

```csharp
GpuTextureReadback ReadbackToCpu();
```

将 GPU 纹理回读到 CPU，返回紧凑 BGRA32 像素数据（供 Avalonia Skia 回退路径直接写入 WriteableBitmap）。
实现负责创建暂存纹理、拷贝、Map 读取，并按源格式转换为 BGRA32。

**Returns**: CPU 回读结果（中立 `GpuTextureReadback`，由调用方 Dispose）。
