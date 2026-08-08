using System.Collections.Concurrent;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Playback;

/// <summary>格式级记忆键：(容器格式, 主视频编码)。用于跨文件复用回退结果，避免每次同格式重走回退。</summary>
/// <remarks>不含任何后端硬编码，全部由实测回退结果填充——对所有后端与 (容器,编解码) 组合一视同仁。</remarks>
internal readonly record struct FormatKey(ContainerFormat Container, VideoCodec Video);

/// <summary>
/// 后端回退调度器 / 中间件（契约纯净：仅依赖 Abstractions + DI.Abstractions，不引用任何具体后端）。
/// 同时实现 <see cref="IMediaPlayerFactory"/>（对外公开的播放器工厂）与 <see cref="IBackendRegistry"/>（只读后端检视）。
/// </summary>
/// <remarks>
/// <para><b>运行时单次判断回退</b>：<see cref="FallbackMediaPlayer.OpenAsync"/> 按 <see cref="Backends"/> 的 DI 注册顺序尝试每个后端组，
/// 命中即把 source 标识 → 后端索引写入 <see cref="Cache"/>（后续直接命中）；全部失败抛 <see cref="MediaBackendUnsupportedException"/>。</para>
/// <para><b>每个播放独立</b>：本工厂为 Singleton，仅持有「后端选择缓存」（接口查找层），绝不共享任何 player 实例。</para>
/// <para><b>lookup 与 instance 不混淆</b>：<see cref="Backends"/> 持有的是 factory 接口（Singleton 无状态服务），不是 player/后端实例；
/// 命中后把这组 factory 接口交给核心 composer 的 <see cref="IMediaPlayerFactory.Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)"/> 去创建 Session。</para>
/// </remarks>
public sealed class BackendFallbackMediaPlayerFactory : IMediaPlayerFactory, IBackendRegistry
{
    private readonly IServiceProvider _sp;
    private readonly ILogger? _logger;
    private readonly IMediaPlayerFactory _composer;
    internal readonly IMediaStreamFactory? _streamFactory;
    internal readonly IFormatDetector? _formatDetector;
    private IReadOnlyList<BackendDescriptor>? _backends;

    /// <summary>source 标识 → 命中后端索引（单次标记，后续直接命中）。跨所有 FallbackMediaPlayer 实例共享。</summary>
    internal readonly ConcurrentDictionary<string, int> Cache = new();

    /// <summary>格式级记忆：(容器, 视频编码) → 命中后端索引。跨所有播放共享，避免同格式重复回退开销。</summary>
    /// <remarks>key 由实测回退结果填充，无硬编码规则；mp4/H264 与 mp4/H265 各自独立记忆，互不污染。</remarks>
    internal readonly ConcurrentDictionary<FormatKey, int> FormatCache = new();

    public BackendFallbackMediaPlayerFactory(
        IServiceProvider sp,
        ILoggerFactory? loggerFactory = null,
        IMediaStreamFactory? streamFactory = null,
        IFormatDetector? formatDetector = null)
    {
        _sp = sp;
        _logger = loggerFactory?.CreateLogger<BackendFallbackMediaPlayerFactory>();
        _streamFactory = streamFactory;
        _formatDetector = formatDetector;
        _composer = sp.GetKeyedService<IMediaPlayerFactory>("composer")
                    ?? throw new InvalidOperationException(
                        "未找到 keyed \"composer\" 的 IMediaPlayerFactory（AddLingFanMedia 未注册核心 composer）。");
    }

    /// <inheritdoc />
    /// <remarks>返回一个尚未打开的 <see cref="FallbackMediaPlayer"/>；后端选择推迟到 <see cref="FallbackMediaPlayer.OpenAsync"/>。</remarks>
    public IMediaPlayer Create()
        => new FallbackMediaPlayer(this, _logger);

    /// <inheritdoc />
    /// <remarks>显式指定后端组：直接委托核心 composer 建 Session（无回退，供高级手动组合）。</remarks>
    public IMediaPlayer Create(
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory = null)
        => _composer.Create(demuxerFactory, videoDecoderFactory, audioDecoderFactory, subtitleDecoderFactory);

    /// <inheritdoc />
    /// <remarks>懒构建：首次访问时按 DI 注册顺序聚合后端组。同一后端须按相同顺序注册 demuxer+video+audio；
    /// subtitle 仅部分后端注册，用 <see cref="Enumerable.ElementAtOrDefault{TSource}(IEnumerable{TSource}, int)"/> 取 null。</remarks>
    public IReadOnlyList<BackendDescriptor> Backends
    {
        get
        {
            if (_backends is null)
            {
                var demuxers = _sp.GetServices<IMediaDemuxerFactory>().ToArray();
                var videos = _sp.GetServices<IVideoDecoderFactory>().ToArray();
                var audios = _sp.GetServices<IAudioDecoderFactory>().ToArray();
                var subs = _sp.GetServices<ISubtitleDecoderFactory>().ToArray();

                // 按索引对齐 demuxer+video+audio（三者各后端都注册且数量一致=对齐正确）。
                // subtitle 仅部分后端注册，【不能】按索引对齐——否则会把 FFmpeg 的 subtitle 错配给 MF 组
                // （subs 数组只有 1 项却处于索引 0，而它实际属于索引 1 的 FFmpeg 组）。
                // 改为按「后端名」字典匹配：先建 subByBackend[后端名]=subtitleFactory，再按 demuxer 的后端名取。
                var subByBackend = new Dictionary<string, ISubtitleDecoderFactory>(StringComparer.Ordinal);
                foreach (var s in subs)
                    subByBackend.TryAdd(NameOf(s), s);

                int n = Math.Min(Math.Min(demuxers.Length, videos.Length), audios.Length);
                var list = new List<BackendDescriptor>(n);
                for (int i = 0; i < n; i++)
                {
                    var backendName = NameOf(demuxers[i]);
                    list.Add(new BackendDescriptor(
                        backendName,
                        demuxers[i], videos[i], audios[i],
                        subByBackend.TryGetValue(backendName, out var sub) ? sub : null));
                }

                _backends = list;
            }
            return _backends;
        }
    }

    /// <summary>从工厂运行时类型名推导友好后端名（去除 Factory / DemuxerFactory / DecoderFactory / SubtitleDecoderFactory 后缀）。</summary>
    /// <remarks>关键：subtitle 工厂名含 SubtitleDecoderFactory，须最先剥离，否则与同后端 demuxer 名不一致（导致 subtitle 错配）。</remarks>
    private static string NameOf(object factory)
    {
        var name = factory.GetType().Name;
        const string subSuffix = "SubtitleDecoderFactory";
        if (name.EndsWith(subSuffix, StringComparison.Ordinal))
            return name.Substring(0, name.Length - subSuffix.Length);
        const string demuxerSuffix = "DemuxerFactory";
        if (name.EndsWith(demuxerSuffix, StringComparison.Ordinal))
            return name.Substring(0, name.Length - demuxerSuffix.Length);
        const string decoderSuffix = "DecoderFactory";
        if (name.EndsWith(decoderSuffix, StringComparison.Ordinal))
            return name.Substring(0, name.Length - decoderSuffix.Length);
        const string factorySuffix = "Factory";
        return name.EndsWith(factorySuffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - factorySuffix.Length)
            : name;
    }
}
