---
title: 异步与同步纪律
sources: ["src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.Dispose.cs", "src/Infrastructure/LingFan.Media.Core/Playback/AudioPipeline.cs", "src/Infrastructure/LingFan.Media.Sources/Streams/NetworkMediaStream.cs"]
symbols: ["IMediaPlayer", "IVideoRenderer", "IAudioOutput", "IMediaClock", "VideoFrame", "AudioFrame"]
lang: zh
---

# 异步与同步纪律

异步与同步在这里不可互换。错误混用会导致死锁、性能退化与泄漏。本页是代码所遵循规则的读者向摘要；逐方法的精确分类见 [API 参考](/zh/api/)。

## 总策略

| 情形 | 形态 |
|---|---|
| 真实 I/O 或阻塞工作（网络、文件、设备、解封装） | 真异步：`await`，返回 `Task` / `ValueTask`。 |
| 纯内存 / CPU / GPU / 原生释放 | 同步：`void` 或同步返回。 |
| 异步契约由纯内存工作实现 | 返回 `Task.CompletedTask` 是正确的，**不是**伪异步。 |

绝不用 `.Wait()`、`.Result` 或 `GetAwaiter().GetResult()` 把阻塞藏进 `Task`。

## 双支持，优先异步

公共 API 在合理处同时支持同步与异步，以异步为默认。两条互补规则：

- **方向 A——真实异步 I/O 必须暴露异步签名。** 若方法内部 await 真实 I/O 却只暴露同步签名，补一套 `XxxAsync`。不要只留一个阻塞调用方的同步签名。
- **方向 B——纯内存工作保持仅同步。** COM 互操作调用、内存态切换、对象池取/还/重置、配置查询不加异步包装；那是伪异步。

资源型与初始化型遵循对称模板：`Dispose` + `DisposeAsync`、`Initialize` + `InitializeAsync`。异步版本是默认；同步版本服务于无法 await 的边界（原生回调、`void` 生命周期覆写）。

## 伪异步的定义

伪异步是返回 `Task` 却在方法体内隐藏阻塞（`.Wait()` / `.Result` / `GetResult()`，或 `Task.Run(() => /* 阻塞 */)`）的方法。把阻塞搬到线程池线程并不使其成为异步。

标志是**隐藏阻塞**，而非返回类型。纯内存方法实现异步接口时返回 `Task.CompletedTask` 是正常且安全的。（`await Task.Run(...)` 也逐例评估，使用时在源码留注释。）

## 必须保持同步的部分

- `IVideoRenderer.Present` 与 GPU 纹理上传（纯 GPU 工作）。
- `IAudioOutput.Submit`（原生背压是有界的正常机制）。
- `SafeHandle.Dispose` 与原生释放调用。
- `IMediaClock` 与同步器（纯内存，用 `lock` / `Interlocked`）。
- 参数/配置 `Initialize(...)` 重载。
- `DecodeAsync` 在热路径保留 `ValueTask`（不要放宽为 `Task`）。

## 管线方法分层

| 层 | 方法 | 形态 |
|---|---|---|
| 视频管线 | Start / Pause / Flush / Stop / Clear | `void`，纯内存。Start 设标志并启动循环；Stop 只发取消信号。 |
| 音频管线 | StartAsync | `Task`，真实 await 设备缓冲预填充，使播放不会始于静默窗。Pause / Flush / Stop / Clear 仍同步。 |
| 管线宿主 | Start / Pause / Flush / Stop | 薄同步委托（Start 跟随音频优先的异步升级）。 |
| `IMediaPlayer` | OpenAsync / SeekAsync / DisposeAsync | 真异步（I/O、线程 join、资源释放）。 |
| `IMediaPlayer` | PlayAsync / PauseAsync / StopAsync | 异步契约，纯内存方法体返回 `Task.CompletedTask`。 |

`Stop` 是 O(1) 取消信号，绝不等待线程。线程 join 在 `DisposeAsync` 中：取消管线并以有界超时 await 其任务，再分步 guarded 释放资源。这一分离使 `Stop` 非阻塞、释放优雅。

## 阻塞点及其异步替代

| 阻塞模式 | 替代 |
|---|---|
| `_pipelineTask.Wait(timeout)` | `cts.Cancel(); await _pipelineTask.WaitAsync(timeout, ct)` |
| `channel.Reader.ReadAsync()...GetResult()` | `await channel.Reader.ReadAsync(ct)` |
| 同步阻塞 `Dequeue()` | `ValueTask<T> DequeueAsync(ct)`；消费端保留同步 `TryDequeue` |
| `Dispose()` 调 `DisposeAsync().GetResult()` | `Dispose()` 内独立的同步清理路径 |

## 同步 Dispose 是兜底

`Dispose()` 的存在是为了在调用方未 await 释放时不泄漏资源。它运行**自己的**同步清理路径；绝不调用 `DisposeAsync().GetResult()` 或 `.Wait()`。推荐路径仍是 `await DisposeAsync()`；同步释放可能丢弃少量在途 GPU 帧，但不泄漏资源。

## 异步锁

- 跨 `await` 的共享状态 → `SemaphoreSlim` 配 `await WaitAsync()`。
- 纯同步共享状态（时钟、同步器）→ 保持 `lock` / `Interlocked`。
- `Channel<T>` 自带线程安全；通常无需额外锁。
- 绝不在 `lock` 内 `await`。方法里有 await 就需要异步锁。

## 调用方契约：禁止 async void

`async void` 被禁止——异常逃逸未被观察，可能使进程崩溃。`async Task` / `async ValueTask` 可以，但 `void` 覆写不能改签名；改为新增独立异步方法。

- `VideoView.OnDetachedFromVisualTree` 是 `void` 覆写 → 另提供 `DisposePlayerAsync()` 供调用方 await；覆写内用同步 `Dispose()` 兜底。
- `VideoView.OnPlayerChanged` 是 `void` 回调 → 在赋值 `Player` 属性**之前**先打开播放器（`await player.OpenAsync()`）。

## HttpClient 来自工厂

任何 `HttpClient` 都经 `IHttpClientFactory.CreateClient()` 获取，而非 `new HttpClient()`（罕见的自定义 handler 情形如 SSL 绕过除外，且必须设 `PooledConnectionLifetime`）。反复构造客户端会因 `TIME_WAIT` 堆积耗尽套接字。Cookie 以请求头传递而非 `CookieContainer`，以兼容工厂的共享 handler。

## 一处获准的例外

FFmpeg 解封装器的原生 AVIO 读回调具有同步 `read_packet` 签名，因此在回调内以 `GetResult()` 桥接到异步流。它跑在 FFmpeg 工作线程（非 UI 线程），是不可避免的原生边界，并在源码中注明——不是有待「优化掉」的伪异步。

## 诊断卡顿与帧停滞

播放停滞、抖动或回弹时，第一假设**不是**「加异步 / `Task.Run`」。在本代码库中，此类症状一贯是同步逻辑错误，按此顺序排查：

1. 队列语义（队头与队尾、peek-then-dequeue 安全性、误丢）。
2. 时钟插值（主时钟插值、音频设备游标量化、呈现阈值自旋安全阀）。
3. COM 线程亲和（具线程亲和的原生对象必须留在单一专用调度器/线程）。
4. 解码器/解封装器契约（例如输出未被取空前拒收输入的变换）。
5. I/O 边界上不该有的同步阻塞。

用异步掩盖同步 bug 会引入跨线程 COM 损坏、退出挂起与新调度竞态——比原 bug 更糟。异步只属于真实 I/O 边界；热路径保持同步。
