using System.Collections.Frozen;

namespace LingFan.Media.Sources;

/// <summary>
/// 自定义流媒体源。包装外部传入的 <see cref="Stream"/>。
/// </summary>
/// <remarks>
/// 不可变对象，线程安全（Stream 本身的线程安全性由调用方保证）。
/// 同时实现 <see cref="IMediaSource"/> 和 <see cref="IMediaSourceMetadata"/>。
/// </remarks>
public sealed class StreamMediaSource : IMediaSource, IMediaSourceMetadata
{
    /// <summary>底层流。</summary>
    public Stream Stream { get; }

    /// <summary>是否在关闭时释放底层流。</summary>
    public bool OwnsStream { get; }

    /// <inheritdoc/>
    public MediaSourceType Type => MediaSourceType.Stream;

    /// <inheritdoc/>
    public string Identifier { get; }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? ContentType { get; }

    /// <inheritdoc/>
    public bool IsLive { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> ExtraFields { get; }

    /// <summary>
    /// 初始化 <see cref="StreamMediaSource"/> 的新实例。
    /// </summary>
    /// <param name="stream">底层流。</param>
    /// <param name="identifier">流标识符（如 "custom-stream-1"）。</param>
    /// <param name="ownsStream">是否在关闭时释放底层流。</param>
    /// <param name="name">显示名称。</param>
    /// <param name="contentType">MIME 类型。</param>
    /// <param name="isLive">是否直播流。</param>
    /// <param name="extraFields">额外元数据。</param>
    public StreamMediaSource(
        Stream stream,
        string identifier,
        bool ownsStream = false,
        string? name = null,
        string? contentType = null,
        bool isLive = false,
        IReadOnlyDictionary<string, string>? extraFields = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("流标识符不能为空。", nameof(identifier));

        Stream = stream;
        Identifier = identifier;
        OwnsStream = ownsStream;
        Name = name;
        ContentType = contentType;
        IsLive = isLive;
        ExtraFields = extraFields ?? FrozenDictionary<string, string>.Empty;
    }
}
