# 帧与资源

## IFrameChannel 与 IFrameSink

这两个接口是管线的**唯一视频帧出口路径**。每个视频终点——无界面计算、Skia 软渲染、D3D11 零拷贝 GPU 呈现——都实现 `IFrameSink` 并通过 `IFrameChannel` 订阅。不存在"有头 vs 无头"的分叉；它们从同一条通道取用帧，仅在终端动作与能力上有所不同。

```csharp
public interface IFrameSink
{
    void OnFrame(VideoFrame frame);   // read-only borrow, never Dispose
}

public interface IFrameChannel
{
    IDisposable Subscribe(IFrameSink sink);   // returns an unsubscribe handle
    void Unsubscribe(IFrameSink sink);
    void Emit(VideoFrame frame);              // fan out to all subscribers
}
```

> **帧所有权：** `Emit` 由管线在 `try` 内调用；该帧由管线在 `finally` 中的 `ReturnFrame` 释放。通道与所有 sink 都是**只读借用者**，绝不可 `Dispose`。在 `OnFrame` 内释放帧会在多播下破坏后续订阅者——这是硬性违规。

公开的 `IMediaPlayer.VideoFrameAvailable` 事件是此通道之上的一个 `Action<VideoFrame>` 门面。高级消费者（录制器、缩略图）可直接实现 `IFrameSink`。

## VideoFrame

命名空间：`LingFan.Media.Abstractions`

`sealed class : IDisposableFrame` —— 一帧已解码的视频帧。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Width` / `Height` | `int` | 帧尺寸。 |
| `Format` | `PixelFormat` | 像素格式。 |
| `Resource` | `IFrameResource?` | 像素缓冲区（CPU 或 GPU）。 |
| `Timestamp` | `TimeSpan` | 显示时间戳。 |
| `Duration` | `TimeSpan` | 帧时长。 |
| `KeyFrame` | `bool` | 关键帧标志。 |
| `Reset()` | `void` | 复用该帧（对象池）。 |
| `Dispose()` | `void` | 释放该帧。 |

## AudioFrame

命名空间：`LingFan.Media.Abstractions`

`sealed class : IDisposableFrame` —— 一帧已解码的音频帧。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Data` | `ReadOnlyMemory<byte>` | PCM 字节。 |
| `SampleRate` | `int` | 采样率。 |
| `Channels` | `int` | 通道数。 |
| `SampleFormat` | `SampleFormat` | S16 / S32 / F32。 |
| `Timestamp` / `Duration` | `TimeSpan` | 计时。 |
| `FrameCount` | `int` | 样本帧数。 |
| `Reset()` / `Dispose()` | `void` | 复用 / 释放。 |

## SubtitleFrame

命名空间：`LingFan.Media.Abstractions`

`Text` / `Start` / `End` / `Style` —— 一条带时间的字幕提示。

## MediaPacket

命名空间：`LingFan.Media.Abstractions`

`sealed class : IDisposable` —— 一个解复用器包（压缩的或预解码的）。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `TrackIndex` | `int` | 所属轨道。 |
| `Data` | `ReadOnlyMemory<byte>` | 包字节。 |
| `Timestamp` / `Duration` | `TimeSpan` | 计时。 |
| `KeyFrame` | `bool` | 关键帧标志。 |
| `DecodedFrameResource` | `IFrameResource?` | 可选的预解码资源（零拷贝路径）。 |
| `HasDecodedFrameResource` | `bool` | 是否附带了已解码资源。 |
| `TakeDecodedFrameResource()` | `IFrameResource?` | 取得已解码资源的所有权。 |
| `Dispose()` | `void` | 释放。 |

## 帧资源接口

| 接口 | 作用 |
|-----------|------|
| `IDisposableFrame` | `IsDisposed` + `Dispose()` —— 一个可释放的帧。 |
| `IFrameResource` | `Width` / `Height` / `Format` + `IDisposable` —— 帧背后的 CPU 或 GPU 像素缓冲区。 |
| `IFramePool<T>` | `Rent()` / `Return()` —— 帧对象池。 |
| `IFramePoolAware<T>` | `SetFramePool()` —— 知悉自身对象池的帧。 |
| `IGpuTextureResource` | `NativeTextureHandle` / `SubresourceIndex` / `ReadbackToCpu` —— 一个 GPU 纹理资源（零拷贝）。 |
| `IGpuDeviceContext` | `ApiType` / `DeviceHandle` / `ContextHandle` / `IsInitialized` / `InitializeAsync` / `GetCapabilities` —— 一个中性的 GPU 设备上下文（共享的 D3D11 设备等）。 |
| `ISharedGpuSurfaceSource` / `ISharedGpuSurfaceSourceFactory` | `HandleKind` / `ConsumerAcquireKey` / `ConsumerReleaseKey` / `TryWriteFrame` —— 跨进程 / 跨 API 的共享 GPU 表面。 |
| `IRealtimePacedOutput` | `bool PaceRealTime { set; }` —— 无界面音频输出实时节奏控制的标记。 |
