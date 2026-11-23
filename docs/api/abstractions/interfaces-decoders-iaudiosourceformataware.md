---
title: "IAudioSourceFormatAware"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioSourceFormatAware.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioSourceFormatAware", "SetSourceFormat"]
lang: "en-US"
---
# IAudioSourceFormatAware

Source: [`IAudioSourceFormatAware.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Decoders/IAudioSourceFormatAware.cs)

源音频格式感知接口。可选实现：**直通型**音频解码器（自身不做解码、由解封装层已产出 PCM）
通过此接口从调用方接收真实的 PCM 输出格式。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioSourceFormatAware
```

## Remarks

**动机**：`OutputSampleRate` / `OutputChannels`
是 MediaPlayer 初始化音频输出设备（如 WASAPI 以固定采样率开设备）的唯一依据。
自带解码器上下文的后端（FFmpeg）能从 codec context 直接得知源参数；
而**直通型**后端（MediaFoundation：SourceReader 内部完成解码，解码器仅包装 PCM 字节）
无从得知参数，若硬编码默认值（44100/2）会与真实媒体不符 → 音高/节奏错乱。
本接口让调用方把解封装层解析出的真实格式注入进来。

**不修改既有接口**（IAudioDecoder 等），由调用方 pattern matching（`is`）检测，
与 `IFramePoolAware{T}` 同一范式；AOT 安全（编译期确定类型，无反射）。

**调用时机**：必须在 `Initialize` 之后、
音频输出设备初始化与首次 `DecodeAsync` 之前调用。

**异步策略**：同步配置——纯内存赋值，无 I/O，不提供 async 重载。

## Members

### SetSourceFormat

```csharp
void SetSourceFormat(int sampleRate, int channels, SampleFormat sampleFormat);
```

注入解封装层实测的 PCM 输出格式。

**Parameters**

- `sampleRate`: 采样率（Hz），必须 &gt; 0。
- `channels`: 声道数，必须 &gt; 0。
- `sampleFormat`: 采样格式。
