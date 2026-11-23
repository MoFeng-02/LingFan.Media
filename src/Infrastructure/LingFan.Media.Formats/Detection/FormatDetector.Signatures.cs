using LingFan.Media.Abstractions;

namespace LingFan.Media.Formats.Detection;

/// <summary>
/// 签名匹配分部：从探测数据识别容器格式（魔数签名 / MPEG-TS 同步字节 / EBML DocType）。
/// </summary>
public partial class FormatDetector
{
    /// <summary>
    /// 从探测数据匹配容器格式签名。
    /// </summary>
    /// <param name="data">已读取的探测数据。</param>
    /// <returns>匹配到的容器格式，或 <see cref="ContainerFormat.Unknown"/>。</returns>
    private static ContainerFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        // MP4: "ftyp" at offset 4
        if (data.Length >= FormatSignature.Mp4Offset + FormatSignature.Mp4Signature.Length &&
            data.Slice(FormatSignature.Mp4Offset, FormatSignature.Mp4Signature.Length)
                .SequenceEqual(FormatSignature.Mp4Signature))
        {
            return ContainerFormat.MP4;
        }

        // AVI: "RIFF" at offset 0 + "AVI " at offset 8
        if (data.Length >= FormatSignature.AviTypeOffset + FormatSignature.AviTypeSignature.Length &&
            data.Slice(FormatSignature.AviRiffOffset, FormatSignature.AviRiffSignature.Length)
                .SequenceEqual(FormatSignature.AviRiffSignature) &&
            data.Slice(FormatSignature.AviTypeOffset, FormatSignature.AviTypeSignature.Length)
                .SequenceEqual(FormatSignature.AviTypeSignature))
        {
            return ContainerFormat.AVI;
        }

        // FLV: "FLV" at offset 0
        if (data.Length >= FormatSignature.FlvOffset + FormatSignature.FlvSignature.Length &&
            data.Slice(FormatSignature.FlvOffset, FormatSignature.FlvSignature.Length)
                .SequenceEqual(FormatSignature.FlvSignature))
        {
            return ContainerFormat.FLV;
        }

        // MKV / WebM: EBML magic at offset 0
        if (data.Length >= FormatSignature.EbmlOffset + FormatSignature.EbmlSignature.Length &&
            data.Slice(FormatSignature.EbmlOffset, FormatSignature.EbmlSignature.Length)
                .SequenceEqual(FormatSignature.EbmlSignature))
        {
            return ParseEbmlDocType(data);
        }

        // MPEG-TS: sync byte 0x47 every 188 bytes
        if (IsMpegTs(data))
        {
            return ContainerFormat.TS;
        }

        return ContainerFormat.Unknown;
    }

    /// <summary>
    /// 检测是否为 MPEG-TS 传输流。
    /// </summary>
    /// <remarks>
    /// MPEG-TS 同步字节 0x47（ASCII 'G'）每 188 字节重复一次。
    /// 扫描探测窗口内所有偏移（不仅是 offset 0），
    /// 以识别从非零偏移开始的录制文件（仅检测 offset 0，存在漏报）。
    /// 为降低误报，要求从某偏移起连续至少 3 个同步字节（间隔 188 字节），与检测规格一致。
    /// </remarks>
    /// <param name="data">探测数据。</param>
    /// <returns>是 MPEG-TS 返回 true。</returns>
    private static bool IsMpegTs(ReadOnlySpan<byte> data)
    {
        const int packetSize = 188;
        const byte sync = FormatSignature.TsSyncByte;

        // 至少需要一个完整包长度（189 字节）才能验证 sync@offset 与 sync@offset+188
        if (data.Length < packetSize + 1)
            return false;

        // 扫描前 64KB（受探测窗口 data.Length 限制），覆盖非零偏移起始的 TS 流
        int scanLimit = Math.Min(data.Length, 65536);

        for (int offset = 0; offset < scanLimit; offset++)
        {
            if (data[offset] != sync)
                continue;

            // 从本偏移起，校验后续 sync byte（间隔 packetSize），连续至少 3 个命中降低误报（对齐检测规格）
            int validCount = 1;
            for (int i = 1; i < 5 && offset + i * packetSize < data.Length; i++)
            {
                if (data[offset + i * packetSize] == sync)
                    validCount++;
            }

            if (validCount >= 3)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 解析 EBML DocType 元素，区分 WebM 和 MKV。
    /// </summary>
    /// <remarks>
    /// <para>EBML 头部结构：</para>
    /// <para>  4 bytes: EBML magic (0x1A 0x45 0xDF 0xA3)</para>
    /// <para>  N bytes: EBML header elements (VINT-encoded ID + VINT-encoded size + data)</para>
    /// <para>DocType 元素 ID = 0x4282，数据为 ASCII 字符串（"webm" 或 "matroska"）。</para>
    /// </remarks>
    /// <param name="data">探测数据（至少包含 EBML magic + 部分 header）。</param>
    /// <returns>WebM 或 MKV；无法确定时默认返回 MKV。</returns>
    private static ContainerFormat ParseEbmlDocType(ReadOnlySpan<byte> data)
    {
        // EBML header 通常在前 256 字节内，搜索 DocType 元素 ID
        int searchLimit = Math.Min(data.Length, 256);
        ReadOnlySpan<byte> docTypeId = FormatSignature.DocTypeElementId;

        // 从 EBML magic 之后开始搜索
        for (int i = FormatSignature.EbmlSignature.Length; i < searchLimit - docTypeId.Length; i++)
        {
            if (data[i] != docTypeId[0] || data[i + 1] != docTypeId[1])
                continue;

            // 找到 DocType 元素 ID (0x42 0x82)
            int dataStart = i + docTypeId.Length;
            if (dataStart >= data.Length)
                break;

            // 解析 VINT size（long 防止恶意超大值溢出）
            long size = ParseEbmlVint(data[dataStart..], out int vintBytes);
            if (size < 0 || vintBytes == 0)
                break;

            // DocType 字符串应该很短（4-8 字节），过大的 size 视为异常
            if (size > 64)
                break;

            int stringStart = dataStart + vintBytes;
            if (stringStart + size > data.Length)
                break;

            ReadOnlySpan<byte> docTypeValue = data.Slice(stringStart, (int)size);

            if (docTypeValue.SequenceEqual(FormatSignature.DocTypeWebM))
                return ContainerFormat.WebM;

            if (docTypeValue.SequenceEqual(FormatSignature.DocTypeMatroska))
                return ContainerFormat.MKV;

            // 未知 DocType 但 EBML 格式——默认为 MKV
            return ContainerFormat.MKV;
        }

        // EBML magic 匹配但未找到 DocType——默认为 MKV
        return ContainerFormat.MKV;
    }

    /// <summary>
    /// 解析 EBML 变长整数（VINT）。
    /// </summary>
    /// <remarks>
    /// <para>EBML VINT 编码：前导零位数的 +1 = 总字节数，剩余位为数据。</para>
    /// <para>  1xxxxxxx: 1 字节，7 data bits</para>
    /// <para>  01xxxxxx xxxxxxxx: 2 字节，14 data bits</para>
    /// <para>  001xxxxx xxxxxxxx xxxxxxxx: 3 字节，21 data bits</para>
    /// </remarks>
    /// <param name="buffer">VINT 起始位置。</param>
    /// <param name="bytesRead">VINT 占用的字节数。</param>
    /// <returns>VINT 值（最大 2^56-1）；解析失败返回 -1。</returns>
    private static long ParseEbmlVint(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;

        if (buffer.IsEmpty)
            return -1;

        byte firstByte = buffer[0];

        // 计算前导零位数确定 VINT 长度
        int lengthBytes = 1;
        byte mask = 0x80;
        while ((firstByte & mask) == 0 && lengthBytes <= 8)
        {
            lengthBytes++;
            mask >>= 1;
        }

        // VINT 最多 8 字节
        if (lengthBytes > 8 || lengthBytes > buffer.Length)
            return -1;

        // 清除长度标记位，读取数据值
        // 使用 long 防止 8 字节 VINT（最大 2^56-1）溢出为负数绕过 size 上限检查
        long value = firstByte & (mask - 1);
        for (int i = 1; i < lengthBytes; i++)
        {
            value = (value << 8) | buffer[i];
        }

        bytesRead = lengthBytes;
        return value;
    }
}