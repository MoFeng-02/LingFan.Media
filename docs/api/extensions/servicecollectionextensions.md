---
title: "ServiceCollectionExtensions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/ServiceCollectionExtensions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ServiceCollectionExtensions", "AddLingFanMedia"]
lang: "en-US"
---
# ServiceCollectionExtensions

Source: [`ServiceCollectionExtensions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/ServiceCollectionExtensions.cs)

DI 注册主入口。提供 `AddLingFanMedia` 扩展方法注册核心媒体服务。

## Declaration

```csharp
namespace LingFan.Media.Extensions;

public static class ServiceCollectionExtensions
```

## Remarks

使用模式：

```csharp
services.AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput();
```

**生命周期模型**：

Infrastructure（Singleton）：无状态工厂 / 共享资源。

- `IMediaStreamFactory` → `MediaStreamFactory`（持有 `IHttpClientFactory`）

- `IMediaDemuxerFactory` → 由各后端 `AddXxx()` 经 TryAddEnumerable 集合注册（不再默认注册，支持多后端并存回退）

- `IMediaPlayerFactory` → `BackendFallbackMediaPlayerFactory`（运行时回退调度；
同一实例同时以 `IBackendRegistry` 对外。核心组装工厂 `MediaPlayerFactory` 以 keyed `"composer"` 注册，供回退调度器选定后端组后建 Session）

Session（Transient）：仅注册 `IMediaPlayer`，内部组件由 Factory 手动 new 不走 DI。

**以下工厂由各子模块扩展方法注册（不在 AddLingFanMedia 中）**：

- `AddFFmpeg()` / `AddMediaFoundation()` / `AddVLCNative()` → 各以 TryAddEnumerable 集合注册
`IMediaDemuxerFactory` / `IVideoDecoderFactory` / `IAudioDecoderFactory` / `ISubtitleDecoderFactory`（可多后端并存，按注册顺序回退）

- `AddD3D11Renderer()` → `IVideoRendererFactory`

- `AddWasapiOutput()` → `IAudioOutputFactory`

此方法为同步配置，无 I/O、无异步。

## Members

### AddLingFanMedia

```csharp
public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    Action<MediaOptions>? configure = null);
```

注册 LingFan.Media 核心服务（工厂 + 播放器）并返回 `MediaBuilder` 供链式注册。

注册 Infrastructure Lifetime（Singleton）服务：

- `IMediaStreamFactory` → `MediaStreamFactory`

- `IFormatDetector` → `FormatDetector`（回退调度器 Open 前轻量探测格式）

- `ICodecRegistry` → `CodecRegistry`（静态映射表，纯内存）

- `IMediaPlayerFactory` → `BackendFallbackMediaPlayerFactory`（同一实例兼以 `IBackendRegistry` 对外）；
核心组装工厂 `MediaPlayerFactory` 以 keyed `"composer"` 注册

- `IMediaDemuxerFactory` **不在此注册**：由各后端 `AddXxx()` 经 TryAddEnumerable 集合注册；
未注册任何后端时 Open 抛 `MediaBackendUnsupportedException`

注册 Session Lifetime（Transient）播放器：

- `IMediaPlayer`（每次解析通过 Factory.Create() 新建）

注册 `IHttpClientFactory`（`AddHttpClient()`）供 `MediaStreamFactory` 网络流连接池管理，
并配置命名 client `"LingFanMedia"` 与 `"LingFanMedia_Insecure"`（均挂 SSRF 连接防护）。

注册 `IOptions<MediaOptions>` 与 `IOptions<MediaPlayerOptions>`（后者传播宿主配置的默认音量）。

日志配置由消费方应用的 Host 负责（Extensions 层只依赖 `Logging.Abstractions`）。

**Parameters**

- `services`: DI 服务集合。
- `configure`: 全局媒体配置回调（可选）。

**Returns**: 媒体构建器，用于链式调用 `AddFFmpeg()` / `AddD3D11Renderer()` 等。

### AddLingFanMedia

```csharp
public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    MediaOptions options);
```

注册 LingFan.Media 核心服务并返回 `MediaBuilder` 供链式注册。

**Parameters**

- `services`: DI 服务集合。
- `options`: 全局媒体配置。

**Returns**: 媒体构建器，用于链式调用 `AddFFmpeg()` / `AddD3D11Renderer()` 等。
