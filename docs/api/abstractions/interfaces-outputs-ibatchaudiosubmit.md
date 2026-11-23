---
title: "IBatchAudioSubmit"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IBatchAudioSubmit.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IBatchAudioSubmit", "SubmitBatch"]
lang: "en-US"
---
# IBatchAudioSubmit

Source: [`IBatchAudioSubmit.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IBatchAudioSubmit.cs)

可选音频批量提交能力（性能优化特性接口）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IBatchAudioSubmit
```

## Remarks

用于把多帧音频在「一次 STA 跨线程往返」内连续提交，消除逐帧 `RunOnSta` 阻塞往返带来的固定开销，
从而让音频供给速率达到实时（修复听感卡顿/掉速）。

属于 `IAudioOutput` 的可选增强：实现者若支持批量提交则实现本接口，
`AudioPipeline` 通过 `is` 探测，不支持时退回逐帧 `Submit`（其他平台行为不变）。

契约层增补签名（未来基准：契约层可动态演进，只增不改优先）。零外部引用，仅依赖本命名空间类型。

## Members

### SubmitBatch

```csharp
void SubmitBatch(IEnumerable<AudioFrame> frames);
```

批量提交音频帧。实现应在一个原生/STA 上下文内连续写入所有帧（含必要的背压等待），
而非为每帧各做一次跨线程往返。
只有**背压超时**（`TimeoutException`：缓冲区长时间无法腾空）才降级为「丢弃该帧、继续后续帧」；
其余异常（声道数/采样率/格式不匹配等参数类错误）必须原样冒泡、中断该批——静默吞掉会把配置错误
变成持续丢帧或杂音，且调用方永远看不到根因。
**取消不属于上述范畴**：仅当存在可取消重载、且由 `CancellationToken` 触发的中止
（`OperationCanceledException`，Stop/Dispose 的正常退出路径）时，实现应放弃本批剩余帧
并尽快返回，**不得**当作错误冒泡；调用方收到正常返回即表示本批已被放弃，按正常退出处理，
不得据此判定失败或记 fail 日志。
不接管帧所有权，调用方负责提交后释放帧。

**Parameters**

- `frames`: 待提交音频帧集合（可能含 null，实现需跳过）。

### SubmitBatch

```csharp
void SubmitBatch(IEnumerable<AudioFrame> frames, CancellationToken ct);
```

批量提交音频帧（可感知取消令牌）。语义同 `SubmitBatch(IEnumerable{AudioFrame})`，
但当 `ct` 触发取消时，实现应尽快放弃阻塞等待（背压/渲染线程握手）并返回，
使调用方（音频管线）能在 Stop/Dispose 时立即退出，避免退出挂起。

取消触发后实现应**尽快正常返回**（内部吞掉 `OperationCanceledException`），
不向调用方抛异常：未写入的帧（含已入队未确认的帧）由调用方按「本批已放弃」统一处理，
正常返回**不**携带「本批已全部写入」的保证。参数类错误仍按
`SubmitBatch(IEnumerable{AudioFrame})` 的规则原样冒泡。

**Parameters**

- `frames`: 待提交音频帧集合（可能含 null，实现需跳过）。
- `ct`: 取消令牌（关闭/停止时触发）。
