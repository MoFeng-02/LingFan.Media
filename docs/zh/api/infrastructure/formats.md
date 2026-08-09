# 格式（`LingFan.Media.Formats`）

容器探测与解复用器路由。`FormatDetector` 是上层唯一依赖的类型（经由 `IFormatDetector` 契约）；其余皆为实现细节或内部辅助。

## DemuxerFactory

Namespace: `LingFan.Media.Formats`

`sealed class : IMediaDemuxerFactory`。Singleton，无状态。每次 `Create` 都返回一个**新的**解复用器实例（每次播放时构建）。

```csharp
public DemuxerFactory(
    ILogger<DemuxerFactory> logger,
    Func<IMediaStream, IMediaDemuxer>? fallbackFactory = null)
```

| 方法 | 返回 | 说明 |
|--------|---------|-------|
| `Create(IMediaStream stream)` | `IMediaDemuxer` | 通过 `FormatDetector.Detect` 探测容器（非致命失败降级为 `Unknown`），然后委托给 `fallbackFactory`。若未注册任何后端 `fallbackFactory`，抛出 `InvalidOperationException`。 |
| `CreateAsync(IMediaStream stream, CancellationToken ct = default)` | `Task<IMediaDemuxer>` | 异步变体 —— 通过 `FormatDetector.DetectAsync` 探测。 |

> 若某个后端（如 `AddFFmpeg()`）覆盖了 `IMediaDemuxerFactory` 注册，则不使用此类。它是 V1 默认路由器。

## FormatDetector

Namespace: `LingFan.Media.Formats.Detection`

`public class : IFormatDetector`。魔数容器识别。使用 `ArrayPool<byte>`（零堆分配）；探测固定的 `4096` 字节窗口并 `Seek` 回起点，以便解复用器从开头重新读取。不可 seek 的流被跳过（返回 `Unknown`，让后端自行探测）。

| 成员 | 返回 | 说明 |
|--------|---------|-------|
| `Detect(IMediaStream stream)`（static） | `ContainerFormat` | 同步探测（在 `DemuxerFactory.Create` 内部使用）。 |
| `DetectAsync(IMediaStream stream, CancellationToken ct = default)`（static） | `Task<ContainerFormat>` | 异步探测。 |
| `DetectProfile(IMediaStream stream)` | `MediaFormatProfile` | `(container, videoCodec)` —— 回退中间件的**格式级记忆**所使用的轻量档案。 |
| `DetectProfileAsync(IMediaStream stream, CancellationToken ct = default)` | `Task<MediaFormatProfile>` | 异步档案探测。 |

探测覆盖：**MP4**（`ftyp`@4）、**EBML/Matroska/WebM**（EBML magic@0 → DocType `webm`/`matroska`）、**AVI**（`RIFF`+`AVI `@0/8）、**MPEG-TS**（同步字节 `0x47` 每 188 字节，扫描多个偏移，≥3 个连续命中以避免误报）、**FLV**（`FLV`@0）。

`ProbeVideoCodec` 按容器扫描探测缓冲区中的已知编解码器字符串（MP4 为 `hvc1`/`avc1`/`av01`/`vp09`/`mp4v`；MKV/WebM 为 `V_MPEGH/ISO/HEVC`/`V_MPEG4/ISO/AVC`/`V_AV1`/`V_VP9`/`V_MPEG2V`；FLV tag `codecId` 7=`H264`、12=`H265`）。未知 → `VideoCodec.Unknown`（无害；中间件回退到全扫描）。

## FormatSignature（internal）

Namespace: `LingFan.Media.Formats.Detection` —— `internal static class`。

`FormatDetector` 使用的魔数表。以 `static readonly byte[]` + `ReadOnlySpan<byte>` 属性形式暴露（零分配比较，AOT 友好）。以只读形式记录；不要直接引用。

| 格式 | 签名 | 偏移 |
|--------|-----------|--------|
| MP4 | `"ftyp"` | 4 |
| MKV / WebM | `0x1A 0x45 0xDF 0xA3`（EBML） | 0 |
| AVI | `"RIFF"` + `"AVI "` | 0 / 8 |
| TS | `0x47`（同步字节，每 188 B） | 0 |
| FLV | `"FLV"` | 0 |
| EBML DocType | `0x42 0x82`，值 `"webm"` / `"matroska"` | — |

## MetadataExtractor

Namespace: `LingFan.Media.Formats.Metadata`

`public static class`。无状态，AOT 友好。从容器格式、轨道列表、时长与额外字段构建 `MediaMetadata`。

```csharp
public static MediaMetadata Extract(
    ContainerFormat format,
    IReadOnlyList<MediaTrack> tracks,
    TimeSpan duration,
    IReadOnlyDictionary<string, string>? extraFields = null)
```

- 以**大小写不敏感**的键查找提取 `title` / `artist` / `album` / `year` / `genre`（容忍后端输出风格）。
- `year` 解析取前 4 位数字（容忍 `"2024-01-01"`）。
- 返回带有 `Duration`、`ContainerFormat` 以及已解析字段 + `ExtraFields` 的 `MediaMetadata`。
