using System.Collections.Frozen;

namespace LingFan.Media.Formats.Metadata;

/// <summary>
/// 媒体元数据提取器。从容器格式、轨道列表和额外字段构建 <see cref="MediaMetadata"/>。
/// </summary>
/// <remarks>
/// <para>静态工具类，无状态，AOT 友好。</para>
/// <para>从 <paramref name="extraFields"/> 中提取常见元数据字段（title/artist/album/year/genre），
/// 键名匹配采用大小写不敏感方式（兼容 FFmpeg 等后端输出的不同大小写风格）。</para>
/// </remarks>
public static class MetadataExtractor
{
    private const string TitleKey = "title";
    private const string ArtistKey = "artist";
    private const string AlbumKey = "album";
    private const string YearKey = "year";
    private const string GenreKey = "genre";

    /// <summary>
    /// 从已知信息构建 <see cref="MediaMetadata"/>。
    /// </summary>
    /// <param name="format">容器格式。</param>
    /// <param name="tracks">轨道列表（Demuxer 解析出的轨道）。</param>
    /// <param name="duration">总时长。</param>
    /// <param name="extraFields">额外元数据键值对（可能包含 title/artist/album/year/genre 等字段）。</param>
    /// <returns>构建的 <see cref="MediaMetadata"/>。</returns>
    /// <exception cref="ArgumentNullException">tracks 为 null。</exception>
    public static MediaMetadata Extract(
        ContainerFormat format,
        IReadOnlyList<MediaTrack> tracks,
        TimeSpan duration,
        IReadOnlyDictionary<string, string>? extraFields = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        IReadOnlyDictionary<string, string> fields =
            extraFields ?? FrozenDictionary<string, string>.Empty;

        return new MediaMetadata
        {
            Title = TryGetValueIgnoreCase(fields, TitleKey),
            Artist = TryGetValueIgnoreCase(fields, ArtistKey),
            Album = TryGetValueIgnoreCase(fields, AlbumKey),
            Year = TryParseYear(TryGetValueIgnoreCase(fields, YearKey)),
            Genre = TryGetValueIgnoreCase(fields, GenreKey),
            Duration = duration,
            ContainerFormat = format,
            ExtraFields = fields
        };
    }

    /// <summary>
    /// 大小写不敏感地查找字典值。
    /// </summary>
    /// <param name="dict">字典。</param>
    /// <param name="key">查找键。</param>
    /// <returns>匹配的值；未找到返回 null。</returns>
    private static string? TryGetValueIgnoreCase(IReadOnlyDictionary<string, string> dict, string key)
    {
        // 先尝试直接查找（O(1)），大多数后端使用小写键
        if (dict.TryGetValue(key, out var value))
            return value;

        // 大小写不敏感遍历查找（O(n)，元数据字段通常很少）
        foreach (var kvp in dict)
        {
            if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }

    /// <summary>
    /// 尝试解析年份字符串。
    /// </summary>
    /// <param name="value">年份字符串。</param>
    /// <returns>解析成功返回年份值；失败返回 null。</returns>
    private static int? TryParseYear(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // 取前 4 位数字（兼容 "2024-01-01" 等格式）
        ReadOnlySpan<char> span = value.AsSpan();
        int start = 0;
        while (start < span.Length && !char.IsDigit(span[start]))
            start++;

        int length = 0;
        while (start + length < span.Length && char.IsDigit(span[start + length]))
            length++;

        if (length < 4)
            return null;

        if (int.TryParse(span.Slice(start, 4), out int year))
            return year;

        return null;
    }
}
