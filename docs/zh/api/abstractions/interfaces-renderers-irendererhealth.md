---
title: "IRendererHealth"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IRendererHealth.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IRendererHealth", "Unhealthy"]
lang: "zh-CN"
---
# IRendererHealth

源码: [`IRendererHealth.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IRendererHealth.cs)

渲染器运行期健康通知契约。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IRendererHealth
```

## 备注

**用途**：渲染器可能在 `Attach` 成功、但运行期持续无法出画
（如 Composition 合成器导入跨设备纹理失败）。此时若仍静默空白且不回退，违反「不能让某渲染器玩不了」
的公平性要求。实现本接口的渲染器在持续失败时触发 `Unhealthy`，宿主据此把该渲染器
拉黑并重建回退链（如 Composition → Skia），保证总有可用路径出画。

**解耦**：定义在 Abstractions，UI 层（VideoView）只按契约订阅，不引用具体渲染器类型。

**触发口径**：本事件是**单次**信号——同一实例至多触发一次，实现须用原子闸门防止
管线线程与 UI 线程并发重复触发。「连续失败」的阈值由实现自定（当前实现取 3～10 次连续呈现/绘制失败），
契约只要求判据是**连续**失败而非累计失败、且阈值 ≥ 1：突发一次失败不应直接拉黑渲染器。

**无恢复通道**：本契约不提供「恢复健康」的通知。宿主收到后应把该实例与其工厂类型一起
移出候选链并回退到下一个渲染器；同一实例不会经本契约回到可用状态，也不得被重新启用。

**AOT 兼容**：仅声明事件，无反射。

## 成员

### Unhealthy

```csharp
event Action? Unhealthy;
```

渲染器运行期不健康（连续无法呈现）时触发一次。
由渲染器在管线线程触发；宿主须切回 UI 线程处理（重建回退链），避免跨线程操作控件。
