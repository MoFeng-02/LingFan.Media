# Extensions / DI（`LingFan.Media.Extensions`）

**组合根**。这是注册该基础设施唯一受支持的入口点。这里的一切都是同步的*配置*（无 I/O，无异步）—— 它构建 DI 容器；实际工作稍后在 `OpenAsync` 内部发生。

## AddLingFanMedia

Namespace: `LingFan.Media.Extensions`

```csharp
public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    Action<MediaOptions>? configure = null);

public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    MediaOptions options);
```

注册核心基础设施并返回 `MediaBuilder` 以便链式注册后端/渲染器/输出。

### 它注册什么

**基础设施（Singleton —— 无状态工厂 / 共享资源）：**

- `IMediaStreamFactory` → `MediaStreamFactory`（持有 `IHttpClientFactory`）。
- `IFormatDetector` → `FormatDetector`（契约干净；中间件仅依赖契约）。
- `ICodecRegistry` → `CodecRegistry`（静态表，纯内存）。
- `AddHttpClient()` 加两个命名客户端：
  - `"LingFanMedia"` —— `SocketsHttpHandler`，`ConnectCallback = SsrfConnectGuard.ConnectAsync`（DNS 固定，关闭重绑定 TOCTOU 窗口）。
  - `"LingFanMedia_Insecure"` —— 相同的保护，但禁用了证书校验（仅用于显式设置 `AllowInsecureHttps` 的源）。
- `IMediaPlayer`（Transient）—— 通过 `IMediaPlayerFactory.Create()` 解析。
- `IOptions<MediaOptions>` 与 `IOptions<MediaPlayerOptions>`（后者将宿主的 `DefaultVolume` 接入 Core 的工厂）。
- 键控 `"composer"` `IMediaPlayerFactory` —— 核心的 `MediaPlayerFactory`（惰性；在所有 `AddXxx()` 调用完成后读取构建器的变换链）。
- `BackendFallbackMediaPlayerFactory`（Singleton），注册**两次，使两个契约都指向同一实例**：
  - `IMediaPlayerFactory` → 回退工厂
  - `IBackendRegistry` → 同一个回退工厂

> **为何两次注册指向同一实例？** 若工厂与注册表解析到*不同*对象，各自会持有自己的回退 `Cache`，破坏命中记忆语义。它们必须是同一个 Singleton。

**此处未注册：** `IMediaDemuxerFactory` / 解码器工厂 / `IVideoRendererFactory` / `IAudioOutputFactory`。后端通过 `TryAddEnumerable` 注册它们（以便多个后端共存并按注册顺序回退）。在返回的 `MediaBuilder` 上调用例如 `.AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`。

## MediaBuilder

Namespace: `LingFan.Media.Extensions`

```csharp
public sealed class MediaBuilder
```

由 `AddLingFanMedia` 返回的流式构建器。其构造函数为 `internal` —— 你只能通过 `AddLingFanMedia` 获得它。

### 属性

| 属性 | 类型 | 说明 |
|----------|------|-------|
| `Services` | `IServiceCollection` | `AddXxx()` 扩展注册进入的 DI 集合。 |
| `Options` | `MediaOptions` | 全局配置对象。 |

### 方法

| 方法 | 返回 | 说明 |
|--------|---------|-------|
| `WithAudioPipeline(AudioPipelineConfig config)` | `MediaBuilder` | 注入音频效果/变换链 + 重置钩子（来自 `config.ToTransforms()` / `config.ResetEffects()`）。 |
| `WithAudioTransforms(IReadOnlyList<Func<AudioFrame, AudioFrame>> transforms, Action? reset = null)` | `MediaBuilder` | 直接注入已组合的音频变换链。 |
| `WithVideoPipeline(VideoPipelineConfig config)` | `MediaBuilder` | 注入视频后处理链 + 重置钩子。 |
| `WithVideoTransforms(IReadOnlyList<Func<VideoFrame, VideoFrame?>> transforms, Action? reset = null)` | `MediaBuilder` | 直接注入已组合的视频变换链。 |

四个方法均返回 `this` 以便链式调用。若均未调用，变换链保持 `null` → **完全兼容 V1**（无后处理）。

> 这些变换字段是 `internal` 的 `Func<...>`/`Action` 委托 —— 中性的 BCL 类型。Core 绝不引用 Video/Audio 模块；依赖倒置成立。

## MediaOptions

Namespace: `LingFan.Media.Extensions`

```csharp
public sealed class MediaOptions
```

全局配置，由 `AddLingFanMedia` 读取并绑定到 `IOptions<MediaOptions>`。

| 属性 | 类型 | 默认值 | 说明 |
|----------|------|---------|-------|
| `DefaultVideoRenderer` | `Type?` | `null`（自动） | |
| `DefaultAudioOutput` | `Type?` | `null`（自动） | |
| `PreferredBackend` | `string?` | `null`（自动） | 例如 `"FFmpeg"`。 |
| `EnableHardwareDecode` | `bool` | `true` | |
| `EnableAutoBackendSelection` | `bool` | `false` | |
| `BufferTargetDuration` | `TimeSpan` | `5 s` | |
| `EnableLogging` | `bool` | `true` | |
| `LogLevel` | `LogLevel` | `Information` | 日志配置存储于此；宿主的日志宿主读取它（Extensions 仅依赖 `Logging.Abstractions`）。 |
| `DefaultVolume` | `float` | `1.0f` | 经由 `IOptions` 传播进 `MediaPlayerOptions.DefaultVolume`。 |

`CopyTo(MediaOptions target)` 是 `internal`，复制所有字段（供 `AddLingFanMedia` 的 `MediaOptions` 重载使用）。

## CodecRegistry（internal）

Namespace: `LingFan.Media.Extensions` —— `internal sealed class : ICodecRegistry`。

一个**静态、AOT 友好**的映射表。实现 `ICodecRegistry`：

- `IsCodecSupported(ContainerFormat, VideoCodec)` / `IsCodecSupported(ContainerFormat, AudioCodec)`
- `GetDefaultVideoCodec(ContainerFormat)` / `GetDefaultAudioCodec(ContainerFormat)`

涵盖 MP4 / MKV / AVI / TS / WebM / FLV 视频+音频编解码器表。注册为 Singleton。你只能通过 `ICodecRegistry` 契约消费它；不要引用具体类型。
