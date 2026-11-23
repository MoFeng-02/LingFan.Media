---
title: "MediaPlayerOptions"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/MediaPlayerOptions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaPlayerOptions", "DefaultVolume", "DefaultMuted", "DefaultPlaybackRate", "EnableHardwareAcceleration", "SoftwareDecodeComplexityThreshold", "VideoFrameQueueCapacity", "AudioSampleQueueCapacity", "LocalBufferTarget", "NetworkBufferTarget", "AudioOutputSampleRate", "AudioOutputChannels", "AudioOutputSampleFormat"]
lang: "en-US"
---
# MediaPlayerOptions

Source: [`MediaPlayerOptions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/MediaPlayerOptions.cs)

播放器默认配置选项（契约层共享配置模型）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaPlayerOptions
```

## Remarks

由 Core 的 MediaPlayerFactory 读取，用于设置新创建播放器的默认值；
由宿主 DI 经 `IOptions<MediaPlayerOptions>` 绑定（Extensions 层 `AddLingFanMedia` 中注册）。

置于 Abstractions 契约层：该配置同时被 Extensions（配置侧）与 Core（消费侧）使用，属多层共享的中立数据模型，
符合契约层“纯数据模型、零外部引用”准则（与 `AudioSettings` / `VideoSettings` 同列）。

## Members

### DefaultVolume

```csharp
public float DefaultVolume { get; set; } = 1.0f;
```

默认音量 (0.0~1.0)。

### DefaultMuted

```csharp
public bool DefaultMuted { get; set; }
```

默认是否静音。

### DefaultPlaybackRate

```csharp
public float DefaultPlaybackRate { get; set; } = 1.0f;
```

默认播放速率。

### EnableHardwareAcceleration

```csharp
public bool EnableHardwareAcceleration { get; set; } = true;
```

是否启用硬件加速解码。

用户显式设为 false 时本意必须真实生效（即便内容超过软件解码阈值也不强制硬解）；
策略仅据此判断是否"尝试"硬解并提供告警。详见 `DecodePreferencePolicy`。

### SoftwareDecodeComplexityThreshold

```csharp
public long? SoftwareDecodeComplexityThreshold { get; set; }
```

软件解码能力阈值（解码成本分）。内容超过此值将"优先硬件加速"且实际未走硬解时告警。
null=使用 `DefaultSoftwareDecodeThreshold`（≈2K60 H265）。

供无头/受限环境按需调高或调低。调低=更早倾向硬解；调高=更宽容忍软件解码。

### VideoFrameQueueCapacity

```csharp
public int VideoFrameQueueCapacity { get; set; } = 30;
```

视频帧队列容量。

### AudioSampleQueueCapacity

```csharp
public int AudioSampleQueueCapacity { get; set; } = 60;
```

音频帧队列容量。

### FromSeconds

```csharp
public TimeSpan LocalBufferTarget { get; set; } = TimeSpan.FromSeconds(5);
```

缓冲目标时长（本地文件）。

### FromSeconds

```csharp
public TimeSpan NetworkBufferTarget { get; set; } = TimeSpan.FromSeconds(30);
```

缓冲目标时长（网络流）。

### AudioOutputSampleRate

```csharp
public int? AudioOutputSampleRate { get; set; }
```

音频输出目标采样率（可选覆盖）。null=使用源媒体采样率。

经 `AudioSettings` 透传至 FFmpegAudioDecoder 重采样。若设置，
解码器输出与 WASAPI 设备均按此率工作，避免节奏/音高错乱。

### AudioOutputChannels

```csharp
public int? AudioOutputChannels { get; set; }
```

音频输出目标声道数（可选覆盖）。null=使用源媒体声道数。

### AudioOutputSampleFormat

```csharp
public SampleFormat? AudioOutputSampleFormat { get; set; }
```

音频输出目标采样格式（可选覆盖）。null=使用源媒体采样格式。
