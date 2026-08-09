# 消费者（`LingFan.Media.Consumers`）

**无头 / 服务端**构建块。它们让 `MediaPlayer` 在**无窗口、无 GPU 设备、无音频设备**的情况下运行 —— 帧与 PCM 通过契约事件流出，供你自己的计算使用（转码、ML 推理、缩略图、频谱分析）。

黄金法则与各处相同：经由 `VideoFrameAvailable` / `AudioDataAvailable` 送达的帧是**只读借用**。在回调内同步复制你需要的内容；绝不 `Dispose` 或在跨线程时保留帧引用。

## ConsumersExtensions

Namespace: `LingFan.Media.Consumers`

带两个 `MediaBuilder` 扩展的 `static class`（同步配置，无 I/O）：

```csharp
public static MediaBuilder AddHeadlessRenderer(this MediaBuilder builder);
public static MediaBuilder AddSilentAudioOutput(this MediaBuilder builder);
```

| 方法 | 注册 | 适用场景 |
|--------|-----------|----------|
| `AddHeadlessRenderer()` | `IVideoRendererFactory` → `NoOpVideoRendererFactory` | 无 `VideoView` / 无窗口 / 无 GPU。**不**注册 `IGpuDeviceContext`（依赖倒置：`MediaPlayer` 仅知道 `IVideoRendererFactory`）。 |
| `AddSilentAudioOutput()` | `IAudioOutputFactory` → `NoOpAudioOutputFactory` | 你不想要声音（CI、转码、ML）。音频样本被直接丢弃。**与"无音频设备"不同** —— 如果你*确实*有设备并想要在无头进程中使用真实声音，请改用 `AddWasapiOutput()`（WASAPI 无需窗口）。 |

将它们配对以构建一个完全无头的播放器：

```csharp
services.AddLingFanMedia()
        .AddFFmpeg()
        .AddHeadlessRenderer()
        .AddSilentAudioOutput();
```

然后通过 `ProcessingFrameSink` 消费帧 / 通过 `ProcessingAudioSink` 消费音频。

## NoOpVideoRenderer

Namespace: `LingFan.Media.Consumers`

`sealed class : IVideoRenderer`。所有方法均为空操作；`PresentationLatency` 为 `TimeSpan.Zero`。即便有帧到达，`Present` 也是安全的（帧通常改走 sink）。关闭式生命周期，无原生资源。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `InitializeAsync(ct)` | `Task` | `Task.CompletedTask`。 |
| `Attach(IRenderTarget)` / `Detach()` / `Present(VideoFrame)` / `Clear()` | `void` | 空操作。 |
| `PresentationLatency` | `TimeSpan` | `Zero`。 |
| `Dispose()` / `DisposeAsync()` | | 空操作。 |

## NoOpAudioOutput

Namespace: `LingFan.Media.Consumers`

`sealed class : IAudioOutput, IRealtimePacedOutput`。丢弃 PCM；不打开设备。实现实时**背压**，以便时钟以正确节奏推进（否则音频会立即提交，同步器会把主时钟猛拉到文件末尾，视频帧则全部被判为"late → dropped"）。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Initialize(int sampleRate, int channels)` | `void` | |
| `Submit(AudioFrame frame)` | `void` | 在 `RealTime` 模式下，锚定首帧并按 `submittedSamples / sampleRate` 节流（`Thread.Sleep` 背压 —— 正常，非伪异步）。在 `Fastest` 模式下（`PaceRealTime == false`）立即返回。若解码器上报的速率为 `0`，则回退到帧自身的采样率。 |
| `Pause()` / `Resume()` / `Flush()` | `void` | `Flush` 重新锚定。 |
| `GetPlaybackPosition()` | `TimeSpan` | `Zero`（无头没有设备游标）。 |
| `Latency` | `TimeSpan` | `Zero`。 |
| `Volume`（get/set）/ `PaceRealTime`（set-only） | `float` / `bool` | 可设置。 |
| `InitializeAsync(ct)` | `Task` | `Task.CompletedTask`。 |
| `Dispose()` / `DisposeAsync()` | | 空操作。 |

## ProcessingFrameSink

Namespace: `LingFan.Media.Consumers`

`sealed class : IHeadlessFrameConsumer`。订阅 `IMediaPlayer.VideoFrameAvailable` 并按资源类型分发 —— **GPU 纹理零拷贝，CPU 帧零分配**。

```csharp
public ProcessingFrameSink(
    Action<VideoFrame>? onFrame = null,
    Action<IGpuTextureResource, VideoFrame>? onGpu = null,
    Action<SoftwareFrameResource, VideoFrame>? onCpu = null)
```

| 成员 | 说明 |
|--------|-------|
| `Attach(IMediaPlayer player)` | 订阅 `VideoFrameAvailable`（幂等 —— 分离之前任何订阅）。 |
| `Detach()` | 取消订阅。 |
| `Consume(VideoFrame frame)` | 若设置了 `onGpu` 且 `frame.Resource is IGpuTextureResource` → 调用 `onGpu`（句柄仅在回调内有效）。否则若设置了 `onCpu` 且 `frame.Resource is SoftwareFrameResource` → 调用 `onCpu`（直接读取 `Span`）。然后调用 `onFrame`（始终，若已设置）。**只读借用** —— 绝不 `Dispose`/保留。 |
| `Dispose()` / `DisposeAsync()` | 分离 + 清空。 |

## ProcessingAudioSink

Namespace: `LingFan.Media.Consumers`

`sealed class : IHeadlessAudioConsumer`。与 `ProcessingFrameSink` 对称，用于音频侧。

```csharp
public ProcessingAudioSink(Action<AudioFrame>? onAudio = null)
```

| 成员 | 说明 |
|--------|-------|
| `Attach(IMediaPlayer player)` | 订阅 `AudioDataAvailable`（幂等）。 |
| `Detach()` | 取消订阅。 |
| `Consume(AudioFrame frame)` | 以**只读借用**方式调用 `onAudio` —— 同步复制 PCM；绝不 `Dispose`/保留。 |
| `Dispose()` / `DisposeAsync()` | 分离 + 清空。 |

> **无头输出，两种形态。** 视频经由 `ProcessingFrameSink`（GPU 句柄或 CPU `Span`）离开；音频经由 `ProcessingAudioSink`（PCM 字节）离开。二者都复用现有的帧/音频路由 —— 管线代码不变；仅终止 sink 不同。这是"单一帧路由"设计的具体回报。
