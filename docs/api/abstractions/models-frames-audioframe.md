---
title: "AudioFrame"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/AudioFrame.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["AudioFrame", "Data", "SampleRate", "Channels", "SampleFormat", "Timestamp", "Duration", "FrameCount", "IsDisposed", "Reset", "Dispose"]
lang: "en-US"
---
# AudioFrame

Source: [`AudioFrame.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/AudioFrame.cs)

音频帧。实现 `IDisposableFrame`。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class AudioFrame : IDisposableFrame
```

## Remarks

Span 访问模式（文档注释）：

```csharp
ReadOnlySpan&lt;float&gt; samples = MemoryMarshal.Cast&lt;byte, float&gt;(frame.Data.Span);
ReadOnlySpan&lt;short&gt; samples = MemoryMarshal.Cast&lt;byte, short&gt;(frame.Data.Span);
```

属性使用 internal set，`Reset` 方法供解码器复用帧实例（帧池化）。

**零拷贝支持**：`Data` 可直接映射原生引用计数 buffer，
所有者以中立 `IDisposable` 经 `Reset` 传入；
`Dispose` 与下一次 `Reset` 均会释放所有者（原生引用计数减一）。
所有者释放后不得再访问 `Data`。

## Members

### Data

```csharp
public ReadOnlyMemory<byte> Data { get; internal set; }
```

PCM 音频数据（只读视图，底层 buffer 由帧拥有或经引用计数共享）。

### SampleRate

```csharp
public int SampleRate { get; internal set; }
```

采样率（Hz，如 44100）。

### Channels

```csharp
public int Channels { get; internal set; }
```

声道数。

### SampleFormat

```csharp
public SampleFormat SampleFormat { get; internal set; }
```

采样格式。

### Timestamp

```csharp
public TimeSpan Timestamp { get; internal set; }
```

时间戳。

### Duration

```csharp
public TimeSpan Duration { get; internal set; }
```

帧持续时间。

### FrameCount

```csharp
public int FrameCount { get; internal set; }
```

本帧包含的采样数。

### IsDisposed

```csharp
public bool IsDisposed { get; private set; }
```

Inherits documentation from its base contract.

### Constructor

```csharp
public AudioFrame(ReadOnlyMemory<byte> data, int sampleRate, int channels,
    SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount);
```

初始化 `AudioFrame` 的新实例。

**Parameters**

- `data`: PCM 音频数据。
- `sampleRate`: 采样率（Hz）。
- `channels`: 声道数。
- `sampleFormat`: 采样格式。
- `timestamp`: 时间戳。
- `duration`: 帧持续时间。
- `frameCount`: 本帧包含的采样数。

### Constructor

```csharp
public AudioFrame();
```

无参构造函数（供 FramePool 工厂创建空壳）。

仅供帧对象池使用。调用方须通过 `Reset` 填充实际数据。

### Reset

```csharp
public void Reset(ReadOnlyMemory<byte> data, int sampleRate, int channels,
    SampleFormat sampleFormat, TimeSpan timestamp, TimeSpan duration, int frameCount,
    IDisposable? dataOwner = null);
```

重置帧状态，供 FramePool 复用。
释放旧的零拷贝所有者（若有），设置新属性值，重置 IsDisposed。

**Parameters**

- `data`: PCM 数据（托管副本或零拷贝原生视图）。
- `sampleRate`: 采样率。
- `channels`: 声道数。
- `sampleFormat`: 采样格式。
- `timestamp`: 时间戳。
- `duration`: 帧持续时间。
- `frameCount`: 采样数。
- `dataOwner`: 零拷贝所有者（可选）。非 null 时 &lt;paramref name="data"/&gt; 映射原生引用计数 buffer。

### Dispose

```csharp
public void Dispose();
```

释放底层 buffer（零拷贝路径：原生引用计数减一）。
