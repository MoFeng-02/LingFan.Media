namespace LingFan.Media.Abstractions;

/// <summary>
/// HTTP Cookie 模型，用于网络流媒体的 Cookie 传递。
/// </summary>
public sealed class MediaCookie
{
    /// <summary>Cookie 名称。</summary>
    public string Name { get; }

    /// <summary>Cookie 值。</summary>
    public string Value { get; }

    /// <summary>域（可能为 null）。</summary>
    public string? Domain { get; }

    /// <summary>路径（可能为 null）。</summary>
    public string? Path { get; }

    /// <summary>是否仅 HTTPS。</summary>
    public bool Secure { get; }

    /// <summary>是否仅 HTTP（不可通过 JS 访问）。</summary>
    public bool HttpOnly { get; }

    /// <summary>
    /// 初始化 <see cref="MediaCookie"/> 的新实例。
    /// </summary>
    /// <param name="name">Cookie 名称。</param>
    /// <param name="value">Cookie 值。</param>
    /// <param name="domain">域（可为 null）。</param>
    /// <param name="path">路径（可为 null）。</param>
    /// <param name="secure">是否仅 HTTPS。</param>
    /// <param name="httpOnly">是否仅 HTTP（不可通过 JS 访问）。</param>
    public MediaCookie(
        string name,
        string value,
        string? domain = null,
        string? path = null,
        bool secure = false,
        bool httpOnly = false)
    {
        Name = name;
        Value = value;
        Domain = domain;
        Path = path;
        Secure = secure;
        HttpOnly = httpOnly;
    }
}
