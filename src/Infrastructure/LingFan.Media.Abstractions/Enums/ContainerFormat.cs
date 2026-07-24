namespace LingFan.Media.Abstractions;

/// <summary>
/// 容器格式类型。
/// </summary>
public enum ContainerFormat : int
{
    /// <summary>MP4 / MOV 容器。</summary>
    MP4,
    /// <summary>Matroska / WebM 容器。</summary>
    MKV,
    /// <summary>AVI 容器。</summary>
    AVI,
    /// <summary>MPEG-TS 传输流。</summary>
    TS,
    /// <summary>WebM 容器（基于 EBML/Matroska）。</summary>
    WebM,
    /// <summary>Flash Video 容器。</summary>
    FLV,
    /// <summary>未知容器格式。</summary>
    Unknown
}
