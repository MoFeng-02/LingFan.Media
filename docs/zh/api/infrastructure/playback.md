# 播放中间件（`LingFan.Media.Playback`）

**后端回退中间件** —— 决定*哪个*已注册后端打开给定源的开放盒式、异常驱动调度器。它是你从 `AddLingFanMedia` 获得的默认 `IMediaPlayerFactory`，但通常你不会自己构造它。

两条原则：

1. **契约干净。** `BackendFallbackMediaPlayerFactory` 仅依赖 `Abstractions` + `Microsoft.Extensions.DependencyInjection.Abstractions`。它绝不引用具体后端、渲染器或 UI 类型。
2. **查找 ≠ 实例。** 工厂持有*工厂接口*（Singleton，无状态）。当选定某个后端时，这些接口被交给核心的 `"composer"` 工厂来构建实际的 `IMediaPlayer` 会话。切勿将描述符与播放器实例混淆。

## BackendFallbackMediaPlayerFactory

Namespace: `LingFan.Media.Playback`

```csharp
public sealed class BackendFallbackMediaPlayerFactory : IMediaPlayerFactory, IBackendRegistry
```

### 构造函数

```csharp
public BackendFallbackMediaPlayerFactory(
    IServiceProvider sp,
    ILoggerFactory? loggerFactory = null,
    IMediaStreamFactory? streamFactory = null,
    IFormatDetector? formatDetector = null)
```

解析键控的 `"composer"` `IMediaPlayerFactory`（若未调用 `AddLingFanMedia` 则抛出 `InvalidOperationException`）。

### 成员

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Create()` | `IMediaPlayer` | 返回**尚未打开的** `FallbackMediaPlayer`；后端选择被推迟到它的 `OpenAsync`。 |
| `Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)` | `IMediaPlayer` | 强制特定后端组 —— 直接委托给核心 composer。无回退。 |
| `Backends` | `IReadOnlyList<BackendDescriptor>` | **惰性**按 DI 注册顺序聚合。`demuxer`/`video`/`audio` 工厂按索引对齐；`subtitle` 工厂按**后端名称**匹配（`Dictionary<string, ISubtitleDecoderFactory>`），以防止错配（例如 FFmpeg 的字幕工厂绝不能匹配到 MF 组）。 |

### 内存缓存（跨实例、跨源）

| 缓存 | 键 | 用途 |
|-------|-----|---------|
| `Cache` | `ConcurrentDictionary<string, int>`（`source.Identifier` → 后端索引） | 文件级记忆：同一源会优先重新打开之前可用的后端。 |
| `FormatCache` | `ConcurrentDictionary<FormatKey, int>`（`(ContainerFormat, VideoCodec)` → 后端索引） | 格式级记忆：同一容器+视频编解码器复用胜出的后端，跳过已知的坏后端。`mp4/H264` 与 `mp4/H265` 被分别记忆。 |

`FormatKey` 是 `internal readonly record struct(ContainerFormat Container, VideoCodec Video)`。

### `NameOf(object factory)`（private）

通过剥离最长匹配后缀来派生友好的后端名称：`SubtitleDecoderFactory` → `DecoderFactory` → `DemuxerFactory` → `Factory`。字幕后缀**首先**被剥离，以便字幕工厂的名称与其同级的 demuxer 名称匹配。

## FallbackMediaPlayer

Namespace: `LingFan.Media.Playback`

```csharp
public sealed class FallbackMediaPlayer : IMediaPlayer
```

一个薄包装器，将每个 `IMediaPlayer` 成员转发给运行时选定的内部播放器（`_active`）。它自身持有**零**后端逻辑。

### 构造函数

```csharp
public FallbackMediaPlayer(BackendFallbackMediaPlayerFactory owner, ILogger? logger)
```

### `OpenAsync(IMediaSource source, CancellationToken ct = default)`

唯一有趣的方法。顺序：

1. 若此前存在 `_active`（重新打开），分离其事件并对其调用 `DisposeAsync`（NativeCallGate 规范 —— 无原生泄漏，无重复事件）。
2. **格式记忆探测**（仅本地文件）：`CreateAsync` 一个探测流，对其 `IFormatDetector.DetectProfile`，以获知 `(container, video)`。对网络/不可 seek 的流跳过。失败降级为完整回退 —— 绝不阻塞播放。
3. 决定**回退起始索引**：`FormatCache`（精确）→ `Cache`（文件）→ `0`（全扫描）。三者都会回绕，因此过期的记忆条目仍会落到其他后端。
4. 从起始索引起尝试每个后端（轮询）。成功时：写入 `FormatCache[(container, realVideo)]` 与 `Cache[source.Identifier]`，应用本地 `Open` 前设置，附加事件（sender = `this`），返回。
5. 遇到 `OperationCanceledException`：处置部分内部对象并重新抛出（尊重取消）。
6. 遇到任何其他异常：记录警告，处置部分内部对象，尝试下一个后端。
7. 若所有后端均失败：抛出 `MediaBackendUnsupportedException(source.Identifier)`。

### 成员

所有 `IMediaPlayer` 成员都转发给 `_active`（当 `_active` 为 `null` 时返回安全默认值 —— `Stopped`/`Zero`/`0`）。本地设置（`_volume`、`_isMuted`、`_playbackRate`、`_mode`）在打开前存储，并在打开时推入内部播放器（`ApplyLocalSettings`）。

`PlayAsync` / `PauseAsync` / `StopAsync` / `SeekAsync` 转发给 `_active`（或当尚未打开时返回 `Task.CompletedTask`）。`Dispose` / `DisposeAsync` 分离事件并委托给内部播放器的 NativeCallGate 保护的释放。

## BackendDescriptor（交叉引用）

定义于契约层 —— 参见 [Player & Session (Abstractions)](/zh/api/abstractions/player#backenddescriptor)。它是 `Backends` 返回的只读、携带工厂接口的描述。

## SyncAction（public enum）

`public enum SyncAction : int`，位于 `LingFan.Media.Core.Clock`（独立文件，**不**嵌套在 `Synchronizer` 内）：`Present` / `Wait` / `Drop`。由 `Synchronizer.CheckVideoFrame` 返回的判定结果。此处记录仅为解释 `MediaPlayer.VideoDroppedFrames` 暴露的丢帧计数器。
