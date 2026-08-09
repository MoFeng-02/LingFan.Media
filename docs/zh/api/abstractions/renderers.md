# 渲染器与输出

## IVideoRenderer

命名空间：`LingFan.Media.Abstractions`

呈现一个视频帧。**线程模型：** `Attach` / `Detach` 在 UI 线程；`Present` / `Clear` 在渲染线程。

```csharp
public interface IVideoRenderer : IMediaComponent
{
    void Attach(IRenderTarget target);
    void Detach();
    void Present(VideoFrame frame);     // synchronous consume
    void Clear();
    TimeSpan PresentationLatency { get; }
}
```

| 成员 | 说明 |
|--------|-------|
| `Attach(target)` | 绑定一个渲染目标（UI 线程）。 |
| `Detach()` | 解绑（UI 线程）。 |
| `Present(frame)` | 呈现一帧（渲染线程）。**同步**——渲染器在返回前完成 GPU 上传 / 拷贝，因此调用方可安全释放该帧。`Present` 是纯 GPU 操作；切勿将其异步化。 |
| `Clear()` | 清除表面（渲染线程）。 |
| `PresentationLatency` | 端到端的 `Present` → 像素可见延迟。同步器据此决定提前多久调用 `Present`，以使帧恰好在音频到达其 PTS 时显现。GPU 路径返回约 1–2 个刷新周期；无界面 / 桩 sink 返回 `TimeSpan.Zero`。 |

## IRenderTarget

`Type` / `HandleType` / `NativeHandle` / `Width` / `Height` / `Scale` —— 描述渲染器绘制的位置。

## IVideoRendererFactory 与 IRendererHealth

| 类型 | 作用 |
|------|------|
| `IVideoRendererFactory` | `Create()` 一个渲染器。 |
| `IRendererHealth` | `event Action? Unhealthy` —— 当渲染器丢失其设备 / 表面时触发。 |

## IAudioOutput

命名空间：`LingFan.Media.Abstractions`

统一的**音频输出端口**。`AudioPipeline` 将每个后端解码出的音频规整为 `Submit`。音频**直接提交给 `IAudioOutput`，绕过同步器**——这种 A/V 不对称是有意为之。

```csharp
public interface IAudioOutput : IMediaComponent
{
    void Initialize(int sampleRate, int channels);
    void Submit(AudioFrame frame);          // does NOT take ownership
    void Pause();
    void Resume();
    ValueTask BeginStreamingAsync(CancellationToken ct);   // default: Resume()
    void Flush();
    TimeSpan GetPlaybackPosition();
    TimeSpan GetPlaybackPositionDirect();   // default: GetPlaybackPosition()
    void ResetPlaybackClock();              // default: empty
    TimeSpan Latency { get; }
    float Volume { get; set; }
}
```

| 成员 | 说明 |
|--------|-------|
| `Initialize(rate, channels)` | 配置（纯内存）。 |
| `Submit(frame)` | 提交一帧。**不获取所有权**——仅同步复制 PCM；调用方释放该帧。在音频线程上调用；缓冲区满时阻塞（COM 背压——一种正常机制，**而非**伪异步）。 |
| `Pause()` / `Resume()` | 播放控制。 |
| `BeginStreamingAsync(ct)` | 在启动设备时钟前预填真实 PCM（preroll），修复播放开始时的静音。默认实现仅 `Resume()`。 |
| `Flush()` | 排空输出缓冲区。 |
| `GetPlaybackPosition()` | 播放位置（用于时钟同步）。 |
| `GetPlaybackPositionDirect()` | 高频、线程安全的位置读取。默认回退到 `GetPlaybackPosition()`。WASAPI 重写它以直接读取设备时钟（零封送）。 |
| `ResetPlaybackClock()` | 重播时钟复位——在 `Ended → Playing` 时，`GetPlaybackPositionDirect()` 应在设备启动前返回 0，这样首帧不会被误判为陈旧而被丢弃。默认空实现；WASAPI 重写。 |
| `Latency` | 输出延迟。 |
| `Volume` | 输出音量 0.0–1.0。 |

## IAudioEngine / IAudioOutputFactory / IBatchAudioSubmit / IRealtimePacedOutput

| 类型 | 作用 |
|------|------|
| `IAudioEngine` | `IsWarm` / `Warmup` / `WarmupAsync` —— 预热音频栈（降低首次播放延迟）。 |
| `IAudioOutputFactory` | `Create()` 一个 `IAudioOutput`。 |
| `IBatchAudioSubmit` | `SubmitBatch(...)` —— 批量提交音频。 |
| `IRealtimePacedOutput` | `bool PaceRealTime { set; }` —— 无界面音频输出实时节奏控制的标记。 |
