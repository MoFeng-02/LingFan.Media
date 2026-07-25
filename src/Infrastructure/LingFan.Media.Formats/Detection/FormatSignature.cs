namespace LingFan.Media.Formats.Detection;

/// <summary>
/// 容器格式魔数签名定义。
/// </summary>
/// <remarks>
/// <para>使用 <see langword="static"/> <see langword="readonly"/> <c>byte[]</c> + <see cref="ReadOnlySpan{T}"/>
/// 属性暴露，零分配比较（AOT 友好，无反射）。</para>
/// <para>签名表：</para>
/// <list type="table">
///   <listheader><term>格式</term><term>签名</term><term>偏移</term></listheader>
///   <item><term>MP4</term><term>"ftyp"</term><term>4</term></item>
///   <item><term>MKV/WebM</term><term>0x1A 0x45 0xDF 0xA3（EBML magic）</term><term>0</term></item>
///   <item><term>AVI</term><term>"RIFF" + "AVI "</term><term>0 / 8</term></item>
///   <item><term>TS</term><term>0x47（同步字节，每 188 字节重复）</term><term>0</term></item>
///   <item><term>FLV</term><term>"FLV"</term><term>0</term></item>
/// </list>
/// <para>WebM 与 MKV 共享 EBML magic，通过 EBML DocType 字段区分（"webm" vs "matroska"）。</para>
/// </remarks>
internal static class FormatSignature
{
    // ── MP4 ──

    /// <summary>MP4 ftyp 盒子标识。</summary>
    private static readonly byte[] s_mp4Signature = "ftyp"u8.ToArray();

    /// <summary>MP4 签名偏移量（box size 之后）。</summary>
    internal const int Mp4Offset = 4;

    /// <summary>MP4 签名。</summary>
    internal static ReadOnlySpan<byte> Mp4Signature => s_mp4Signature;

    // ── MKV / WebM（EBML）──

    /// <summary>EBML magic number（MKV 和 WebM 共享）。</summary>
    private static readonly byte[] s_ebmlSignature = [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>EBML 签名偏移量。</summary>
    internal const int EbmlOffset = 0;

    /// <summary>EBML 签名。</summary>
    internal static ReadOnlySpan<byte> EbmlSignature => s_ebmlSignature;

    // ── AVI ──

    /// <summary>AVI RIFF 标识。</summary>
    private static readonly byte[] s_aviRiffSignature = "RIFF"u8.ToArray();

    /// <summary>AVI 类型标识（含尾部空格）。</summary>
    private static readonly byte[] s_aviTypeSignature = "AVI "u8.ToArray();

    /// <summary>RIFF 签名偏移量。</summary>
    internal const int AviRiffOffset = 0;

    /// <summary>AVI 类型签名偏移量（RIFF + 4 字节 size 之后）。</summary>
    internal const int AviTypeOffset = 8;

    /// <summary>AVI RIFF 签名。</summary>
    internal static ReadOnlySpan<byte> AviRiffSignature => s_aviRiffSignature;

    /// <summary>AVI 类型签名。</summary>
    internal static ReadOnlySpan<byte> AviTypeSignature => s_aviTypeSignature;

    // ── MPEG-TS ──

    /// <summary>MPEG-TS 同步字节。</summary>
    internal const byte TsSyncByte = 0x47;

    /// <summary>MPEG-TS 包大小（188 字节）。</summary>
    internal const int TsPacketSize = 188;

    // ── FLV ──

    /// <summary>FLV 签名。</summary>
    private static readonly byte[] s_flvSignature = "FLV"u8.ToArray();

    /// <summary>FLV 签名偏移量。</summary>
    internal const int FlvOffset = 0;

    /// <summary>FLV 签名。</summary>
    internal static ReadOnlySpan<byte> FlvSignature => s_flvSignature;

    // ── EBML DocType（区分 WebM 和 MKV）──

    /// <summary>EBML DocType 元素 ID（0x4282）。</summary>
    private static readonly byte[] s_docTypeElementId = [0x42, 0x82];

    /// <summary>EBML DocType 元素 ID。</summary>
    internal static ReadOnlySpan<byte> DocTypeElementId => s_docTypeElementId;

    /// <summary>EBML DocType 值：WebM。</summary>
    private static readonly byte[] s_docTypeWebM = "webm"u8.ToArray();

    /// <summary>EBML DocType 值：Matroska。</summary>
    private static readonly byte[] s_docTypeMatroska = "matroska"u8.ToArray();

    /// <summary>DocType 值：WebM。</summary>
    internal static ReadOnlySpan<byte> DocTypeWebM => s_docTypeWebM;

    /// <summary>DocType 值：Matroska。</summary>
    internal static ReadOnlySpan<byte> DocTypeMatroska => s_docTypeMatroska;
}
