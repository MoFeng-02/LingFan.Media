# 异步与同步纪律

本指南阐述 LingFan.Media 的并发模型。违反这些准则会导致死锁、性能回退与内存泄漏。

## 总体策略

| Situation | Correct choice |
|-----------|----------------|
| Real I/O or blocking (network, file, device, decode, 管线 `await`) | **Real async** — `await`, return `Task` / `ValueTask` |
| Pure in-memory / CPU / GPU / native release | **Sync** — `void` or sync return |
| Interface contract requires `Task` but impl is pure in-memory | Return `Task.CompletedTask` (acceptable, **not** pseudo-async) |

**绝对禁止：** 通过 `.Wait()` / `.Result` / `GetAwaiter().GetResult()` / `Task.Run(() => syncBlock)` 把阻塞藏进 `Task`。

## 同步/异步双支持（优先异步）

- **方向 A —— 真正的异步 I/O → 你必须添加一个 `XxxAsync` 签名。** 如果一个方法内部有真正的 `await` 却只暴露同步签名，请添加异步重载。不要逼迫调用方陷入 `.Wait()` / `.Result`。
- **方向 B —— 纯内存操作 → 保持仅同步，不要添加异步。** COM interop 调用、内存中的解码器状态切换、pool 的 `Get` / `Return`、`Reset`、config / state 查询——添加返回 `Task` 的 `async` 是伪异步；请勿如此。

资源类型遵循内置模板：`Dispose` + `DisposeAsync`、`Initialize` + `InitializeAsync`。默认优先的公开方法是异步版本；同步版本是给无法 `await` 的边界（原生回调、`void` 生命周期重写）的回退。

**助记：** 内部有真正的 `await` → 给出异步签名（优先）；内部是纯内存操作 → 只给同步签名。不要越界。

## 伪异步的硬性定义

伪异步 = 返回 `Task` 但其函数体包含**隐藏的阻塞**（`.Wait()` / `.Result` / `GetResult()` / `Task.Run(() => syncBlock)`），把阻塞从调用方线程转移到线程池线程。阻塞的本质并未改变。

> **`Task.CompletedTask` 不是伪异步。** 伪异步的标志是*隐藏的阻塞*，而非返回 `Task.CompletedTask`。一个纯内存的方法通过返回 `Task.CompletedTask` 来实现异步接口，这是正常且安全的。

## 必须保持同步（真正的同步，不要异步化）

- `IVideoRenderer.Present` / D3D11 GPU 上传（纯 GPU 工作）
- `IAudioOutput.Submit`（原生 COM 背压，有界——一种正常机制）
- `SafeHandle.Dispose` / FFmpeg 原生释放（`avcodec_close` 等）
- `MediaClock` / `Synchronizer`（纯内存，使用 `lock` / `Interlocked`）
- 带参数的 `Initialize(int,int)` / `Initialize(codec, settings)`
- `DecodeAsync` 返回 `ValueTask`（热路径——保留 `ValueTask`，不要改成 `Task`）

## 管线 方法分层

| Method | Returns | Reason |
|--------|---------|--------|
| `VideoPipeline.Start()` | `void` | set flag + `Task.Run` fire-and-forget, pure in-memory |
| `VideoPipeline.Pause()` / `Flush()` / `Stop()` / `Clear()` | `void` | pure in-memory; `Stop()` only calls `cts.Cancel()`, does **not** wait for the thread |
| `AudioPipeline.StartAsync()` | `Task` | a fresh audio start truly `await`s device buffer preroll (`BeginStreamingAsync`) to fix start-of-playback silence |
| `MediaPipelineHost.Start/Pause/Flush/Stop` | `void` | thin wrapper delegating to 管线 sync methods |
| `IMediaPlayer.OpenAsync` | `Task` | real `await` (解复用器 open + buffer start + stream read) |
| `IMediaPlayer.PlayAsync/PauseAsync/StopAsync` | `Task` | pure in-memory → `Task.CompletedTask` |
| `IMediaPlayer.SeekAsync` | `Task` | real `await` (解复用器 seek depends on stream seek/read) |
| `IMediaPlayer.DisposeAsync` | `ValueTask` | real `await` (thread join + GPU flush + network close) |

> `Stop()` 是一个快速信号（`cts.Cancel`）；`DisposeAsync()` 是优雅的等待（join + release）。职责是分离的。

## 线程 join 属于 DisposeAsync，而非 Stop

`DisposeAsync` 第 1 步 join 管线 线程：`cts.Cancel(); await _pipelineTask.WaitAsync(5s, ct)`。其约 11 个步骤中的每一个都被包在各自的 `try/catch` 中。

## 阻塞 → 真正异步的替换

| Old blocking | Real-async replacement |
|-------------|------------------------|
| `_pipelineTask.Wait(5s)` | `cts.Cancel(); await _pipelineTask.WaitAsync(5s, ct)` |
| `channel.Reader.ReadAsync().AsTask().GetAwaiter().GetResult()` | `await channel.Reader.ReadAsync(ct)` |
| sync `void Dequeue()` blocks | `ValueTask<T> DequeueAsync(ct)` wrapping `await ReadAsync`; keep `TryDequeue` sync |
| `Dispose()` calls `DisposeAsync().GetResult()` | sync `Dispose()` does its own sync cleanup; **never** call `DisposeAsync().GetResult()` inside sync `Dispose` |

## 同步 Dispose 回退

- 同步 `Dispose()` 是一张安全网（确保在调用方没有 `await` 时也不会泄漏）。
- **硬性规则：** 同步 `Dispose()` 必须**绝不**调用 `DisposeAsync().GetResult()` / `.Wait()`（伪异步）。
- 同步 `Dispose()` 做它自己的同步清理（调用 `管线.Stop()` + 直接释放原生资源）。
- 如果 GPU 队列未被 flush，可能会丢失少量帧——但**资源永不泄漏**。
- 优先路径始终是 `await DisposeAsync()`；同步 `Dispose()` 纯粹是回退。

## 异步锁规则

- 跨 `await` 的共享状态 → `SemaphoreSlim` + `await sem.WaitAsync()`。
- 纯同步的共享状态（Clock / Synchronizer）→ 保留 `lock` / `Interlocked`，**不要**切换到 `SemaphoreSlim`。
- `Channel<T>` 自身线程安全；通常无需加锁。
- **硬性规则：** 绝不在 `lock` 内 `await`。

## `async void` 绝对禁止

任何 `void` 方法（事件回调、生命周期重写）都**不能**被做成 `async`——异常会被吞掉，调用方无法 `await`，并且进程可能崩溃。请改用 `async Task` / `async ValueTask`，并为 `void` 重写额外添加一个异步方法。

示例：

- `VideoView.OnDetachedFromVisualTree` 是一个 `void` 重写 → 为调用方添加 `public async ValueTask DisposePlayerAsync()`；该 `void` 重写作为回退调用同步 `Dispose()`。
- `VideoView.OnPlayerChanged` 是一个 `void` 回调 → 调用方应在绑定 `Player` 属性*之前* `await player.OpenAsync()`。

## HttpClient 必须使用 IHttpClientFactory

任何需要 `HttpClient` 的地方，都通过 `IHttpClientFactory.CreateClient()` 获取——**绝不 `new HttpClient()`**（极少数设置 `PooledConnectionLifetime` 的绕过 SSL 的情况除外）。`MediaStreamFactory` 是一个持有 `IHttpClientFactory` 引用的 Singleton。网络流使用池化的 client。Cookie 通过请求头传递，而非 `CookieContainer`，以与共享 handler 保持兼容。


