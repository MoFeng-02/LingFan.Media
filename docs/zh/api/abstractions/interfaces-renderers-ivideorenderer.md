---
title: "IVideoRenderer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRenderer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoRenderer", "Attach", "Detach", "Present", "Clear", "PresentationLatency"]
lang: "zh-CN"
---
# IVideoRenderer

源码: [`IVideoRenderer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRenderer.cs)

视频渲染器接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoRenderer : IMediaComponent
```

## 备注

线程模型：

- Attach / Detach 在 UI 线程调用
- Present / Clear 在渲染线程调用

IFrameResource 非线程安全，需在单线程内使用。

## 成员

### Attach

```csharp
void Attach(IRenderTarget target);
```

绑定渲染目标。UI 线程调用。

**参数**

- `target`：渲染目标。

### Detach

```csharp
void Detach();
```

解绑渲染目标。UI 线程调用。

### Present

```csharp
void Present(VideoFrame frame);
```

呈现一帧。渲染线程调用。
Present 为同步消费——Renderer 在返回前完成 GPU 资源上传/拷贝，
调用方即可安全释放帧；若某 Renderer 需异步保留帧，应由该 Renderer 自行接管所有权。

**参数**

- `frame`：待呈现的视频帧；返回后调用方即可释放（除非该 Renderer 文档声明接管所有权）。

### Clear

```csharp
void Clear();
```

清除当前画面。渲染线程调用。

### PresentationLatency

```csharp
TimeSpan PresentationLatency { get; }
```

端到端「调用 `Present` → 画面真正可见」的延迟（即 Present 到像素上屏的真实耗时）。

视频同步据此决定**提前多少调用 Present**，使画面恰好在音频到达该帧 PTS 时可见 ——
这是音画对齐的真正物理依据，绝非任意同步容差。

有头 GPU 路径（D3D11/Vulkan 等，vsync 呈现）应返回显示器**刷新周期**
（含 DWM/合成器缓冲，约 1~2 个刷新周期）；无头 / 测试桩 / 纯计算 sink 无真实上屏延迟，返回
`Zero`。
