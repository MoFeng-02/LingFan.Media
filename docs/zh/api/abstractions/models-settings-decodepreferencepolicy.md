---
title: "DecodePreferencePolicy"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/DecodePreferencePolicy.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["DecodePreferencePolicy", "ComputeScore", "ExceedsSoftwareDecodeCapability", "BitDepthOf"]
lang: "zh-CN"
---
# DecodePreferencePolicy

源码: [`DecodePreferencePolicy.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/DecodePreferencePolicy.cs)

解码偏好策略：以"解码成本分"替代纯分辨率阈值，判断内容是否超出软件解码的实时能力，
从而决定应"优先硬件加速"。成本分 = 宽×高×帧率 × 编码复杂度权重 × 位深权重。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public static class DecodePreferencePolicy
```

## 备注

设计动机：纯分辨率阈值会漏掉高帧率（如 1440p@120）与高复杂度编码（AV1/HEVC）场景；
10-bit 比 8-bit 软件解码更贵约 30%。成本分综合三者，给出更稳的判据。经验值：
1080p@60 H265≈199M、1440p@60 H265≈354M、4K@60 H264≈498M、4K@60 H265≈796M（单位：加权像素/秒）。

语义为"优先硬件加速"（PreferHardware），而非"强制"：本策略只决定硬解是否应被尝试；
是否真能走硬解取决于运行时是否有可用 GPU 设备上下文，且硬解失败时必须优雅回落软解（既有保证）。
用户显式关闭硬件加速的意图始终优先——策略不会覆盖 `EnableHardwareAcceleration` 的 false。

纯静态、无反射、无分配，符合 AOT 零警告要求。

## 成员

### 300_000_000L

```csharp
public const long DefaultSoftwareDecodeThreshold = 300_000_000L;
```

默认软件解码能力阈值（成本分）。≈ 2K (2560×1440)@60 H265 8-bit 的加权值，
作为"建议优先硬解"的默认起点。超过即视为软件解码在典型 CPU 上难以实时。

### ComputeScore

```csharp
public static long ComputeScore(VideoTrackInfo? info, VideoCodec? codec);
```

计算视频轨道的解码成本分（加权像素/秒）。返回 0 表示信息不足（如轨道信息为空）。

**参数**

- `info`：视频轨道详情（宽/高/帧率/像素格式）。
- `codec`：视频编码；未知时按基准权重计。

### ExceedsSoftwareDecodeCapability

```csharp
public static bool ExceedsSoftwareDecodeCapability(VideoTrackInfo? info, VideoCodec? codec, long? threshold = null);
```

内容是否超过软件解码实时能力阈值（应优先硬件加速）。

**参数**

- `info`：视频轨道详情。
- `codec`：视频编码。
- `threshold`：阈值；null 使用 &lt;see cref="DefaultSoftwareDecodeThreshold"/&gt;。

### BitDepthOf

```csharp
public static int BitDepthOf(PixelFormat format) => format switch;
```

像素格式的位深（视频域：10-bit 格式记 10，其余记 8）。

**参数**

- `format`：像素格式。

**返回**：位深：10-bit 格式返回 10，其余返回 8。
