---
title: "IAudioEngine"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioEngine.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioEngine", "IsWarm", "Warmup", "WarmupAsync"]
lang: "en-US"
---
# IAudioEngine

Source: [`IAudioEngine.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioEngine.cs)

音频引擎（Infrastructure 级长期原生资源）。表示"进程内唯一、跨播放会话共享"的底层音频子系统连接，
与 `IAudioOutput`（Session 级、每次 `OpenAsync` 新建）严格分层。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioEngine : IDisposable, IAsyncDisposable
```

## Remarks

**为什么需要这一层**：某些平台的音频子系统在"进程内第一个音频流建立"时会付出一次性冷启动开销
（典型如 Windows WASAPI：首个 `IAudioClient.Initialize` 触发 audiodg.exe 拉起，耗时约数秒级）。
该开销的作用域是**操作系统音频引擎**而非某个播放会话，因此它必须由一个长生命周期对象承担一次，
而不是让每个 Session 反复支付。

**DI 生命周期**：**Singleton**。与 GPU 设备的分层范式一致——
`ID3D11Device`/`VkDevice` 是 Singleton 共享，而 `SwapChain`/`RenderTarget` 是 Session 级；
对应到音频：**引擎/设备端点句柄是 Singleton，`IAudioOutput` 是 Transient**。
绝不可把 Session 状态对象（持有播放线程、缓冲区、音量）升为 Singleton。

**与 Session 的耦合度**：零。`IAudioOutput` 不引用本接口，也不依赖其内部原生对象；
二者仅通过"操作系统音频引擎已处于热态"这一**进程级副作用**间接协作。这样多轨播放（N 个并发 Session）
天然由 DI 各自 new/dispose，互不污染，也不需要任何手工引用计数。

**调用时机由 host 决定**：库只暴露能力，不代管"何时预热"。宿主应在启动画面/加载界面期调用
`WarmupAsync` 吸收冷启动成本；不调用则维持原行为（成本落在首次播放）。

**失败语义**：预热失败一律视为"未预热"，绝不能中断宿主启动，也不影响后续播放
（正式播放路径仍会完整初始化音频）。

## Members

### IsWarm

```csharp
bool IsWarm { get; }
```

音频引擎是否已处于热态（预热成功且长期资源仍存活）。

### Warmup

```csharp
void Warmup();
```

同步预热音频引擎。幂等：重复调用直接返回。

会阻塞调用线程直到冷启动完成（可能数秒），不要在 UI 线程调用；
UI 宿主请用 `WarmupAsync`。

### WarmupAsync

```csharp
Task WarmupAsync(CancellationToken ct = default);
```

异步预热音频引擎。幂等：重复调用直接返回（已在预热中则复用同一次进行中的操作）。

**Parameters**

- `ct`: 取消令牌。取消只中止&lt;b&gt;等待&lt;/b&gt;，已投递的原生初始化不会被回滚。

**Returns**: 预热完成（或已在热态直接完成）时结束的任务；失败语义见 remarks（视为未预热，不中断宿主启动）。
