using System.Collections.Frozen;
using System.IO;

namespace LingFan.Media.Sources;

/// <summary>
/// 本地文件媒体源。
/// </summary>
/// <remarks>
/// 不可变对象，线程安全（所有属性在构造时确定后只读）。
/// 同时实现 <see cref="IMediaSource"/> 和 <see cref="IMediaSourceMetadata"/>。
/// </remarks>
public sealed class FileMediaSource : IMediaSource, IMediaSourceMetadata
{
    /// <summary>文件完整路径。</summary>
    public string Path { get; }

    /// <inheritdoc/>
    public MediaSourceType Type => MediaSourceType.File;

    /// <inheritdoc/>
    public string Identifier => Path;

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? ContentType { get; }

    /// <inheritdoc/>
    public bool IsLive => false;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> ExtraFields { get; } =
        FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// 初始化 <see cref="FileMediaSource"/> 的新实例。
    /// </summary>
    /// <param name="path">文件完整路径。</param>
    /// <param name="name">显示名称（null 时自动从路径提取文件名）。</param>
    /// <param name="contentType">MIME 类型（如 "video/mp4"）。</param>
    public FileMediaSource(string path, string? name = null, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("文件路径不能为空。", nameof(path));

        Path = System.IO.Path.GetFullPath(path);
        Name = name ?? System.IO.Path.GetFileName(path);
        ContentType = contentType;
    }
}
