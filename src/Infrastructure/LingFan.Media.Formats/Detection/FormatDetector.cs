using System.Buffers;

namespace LingFan.Media.Formats.Detection;

/// <summary>
/// 容器格式探测器。通过读取流头部魔数签名识别媒体容器格式。
/// </summary>
/// <remarks>
/// <para>使用 <see cref="ArrayPool{T}"/> 池化分配探测缓冲区，零堆分配。</para>
/// <para>探测读取量固定为 <see cref="ProbeBufferSize"/>（4096 字节），
/// 足够覆盖所有容器格式签名，不会因恶意文件头而耗尽内存。</para>
/// <para>探测完成后 <see cref="IMediaStream.Seek"/> 回起始位置，供 Demuxer 重新读取。</para>
/// <para>不可定位流（<see cref="IMediaStream.CanSeek"/> = false）跳过探测，返回 <see cref="ContainerFormat.Unknown"/>，
/// 由后端（如 FFmpeg）自行探测。</para>
/// <para><b>同步 I/O 说明</b>：Detect 在同步上下文（<see cref="DemuxerFactory.Create"/>）中调用，
/// 使用 <see cref="IMediaStream.Read"/> 同步读取，无 ValueTask 阻塞语义问题。
/// 网络流会阻塞调用线程，但不在 UI 线程执行。</para>
/// </remarks>
public static class FormatDetector
{
    /// <summary>
    /// 探测缓冲区大小（字节）。4096 足够覆盖所有容器格式的签名
    /// （最大签名：EBML DocType 解析需扫描前 256 字节，均在 4096 范围内）。
    /// </summary>
    private const int ProbeBufferSize = 4096;

    /// <summary>
    /// 探测媒体流的容器格式。
    /// </summary>
    /// <param name="stream">媒体数据流（必须可定位）。</param>
    /// <returns>识别到的 <see cref="ContainerFormat"/>；无法识别时返回 <see cref="ContainerFormat.Unknown"/>。</returns>
    /// <exception cref="ArgumentNullException">stream 为 null。</exception>
    public static ContainerFormat Detect(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // 不可定位流跳过探测——读出的数据无法回退，后端会错过流头部
        if (!stream.CanSeek)
            return ContainerFormat.Unknown;

        long originalPosition = stream.Position;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProbeBufferSize);

        try
        {
            int totalRead = 0;

            // 循环读取直到填满缓冲区或到达流末尾
            while (totalRead < ProbeBufferSize)
            {
                // 使用同步 Read，避免 ValueTask 阻塞语义问题
                int read = stream.Read(buffer.AsSpan(totalRead, ProbeBufferSize - totalRead));
                if (read == 0)
                    break; // 流结束
                totalRead += read;
            }

            if (totalRead == 0)
                return ContainerFormat.Unknown;

            return DetectFormat(buffer.AsSpan(0, totalRead));
        }
        catch (IOException)
        {
            // I/O 错误——无法读取探测数据，由调用方决定是否降级
            return ContainerFormat.Unknown;
        }
        catch (OperationCanceledException)
        {
            // 操作被取消——视为无法识别
            return ContainerFormat.Unknown;
        }
        finally
        {
            // 探测后 Seek 回起始位置，供 Demuxer 从头读取
            try
            {
                stream.Seek(originalPosition, SeekOrigin.Begin);
            }
            catch (IOException)
            {
                // 尽力而为——Seek 失败由下游 Demuxer 处理
            }
            catch (NotSupportedException)
            {
                // 流不支持 Seek（理论不应发生，CanSeek 已检查）
            }
            catch (ObjectDisposedException)
            {
                // 流已被释放
            }
            catch (Exception)
            {
                // finally 块中的最佳努力清理——捕获所有剩余异常
                // 确保 ArrayPool.Return 必定执行，防止缓冲区泄漏
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 探测媒体流的容器格式。
    /// </summary>
    /// <param name="stream">媒体数据流（必须可定位）。</param>
    /// <returns>识别到的 <see cref="ContainerFormat"/>；无法识别时返回 <see cref="ContainerFormat.Unknown"/>。</returns>
    /// <exception cref="ArgumentNullException">stream 为 null。</exception>
    public static async Task<ContainerFormat> DetectAsync(IMediaStream stream, CancellationToken ct = default)
    {

        ArgumentNullException.ThrowIfNull(stream);

        // 不可定位流跳过探测——读出的数据无法回退，后端会错过流头部
        if (!stream.CanSeek)
            return ContainerFormat.Unknown;

        long originalPosition = stream.Position;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProbeBufferSize);

        try
        {
            int totalRead = 0;

            // 循环读取直到填满缓冲区或到达流末尾
            while (totalRead < ProbeBufferSize)
            {
                // 异步读取，支持 CancellationToken，不阻塞线程
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, ProbeBufferSize - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    break; // 流结束
                totalRead += read;
            }

            if (totalRead == 0)
                return ContainerFormat.Unknown;

            return DetectFormat(buffer.AsSpan(0, totalRead));
        }
        catch (IOException)
        {
            // I/O 错误——无法读取探测数据，由调用方决定是否降级
            return ContainerFormat.Unknown;
        }
        catch (OperationCanceledException)
        {
            // 操作被取消——视为无法识别
            return ContainerFormat.Unknown;
        }
        finally
        {
            // 探测后 Seek 回起始位置，供 Demuxer 从头读取
            try
            {
                stream.Seek(originalPosition, SeekOrigin.Begin);
            }
            catch (IOException)
            {
                // 尽力而为——Seek 失败由下游 Demuxer 处理
            }
            catch (NotSupportedException)
            {
                // 流不支持 Seek（理论不应发生，CanSeek 已检查）
            }
            catch (ObjectDisposedException)
            {
                // 流已被释放
            }
            catch (Exception)
            {
                // finally 块中的最佳努力清理——捕获所有剩余异常
                // 确保 ArrayPool.Return 必定执行，防止缓冲区泄漏
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 从探测数据匹配容器格式签名。
    /// </summary>
    /// <param name="data">已读取的探测数据。</param>
    /// <returns>匹配到的容器格式，或 <see cref="ContainerFormat.Unknown"/>。</returns>
    private static ContainerFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        // ── MP4: "ftyp" at offset 4 ──
        if (data.Length >= FormatSignature.Mp4Offset + FormatSignature.Mp4Signature.Length &&
            data.Slice(FormatSignature.Mp4Offset, FormatSignature.Mp4Signature.Length)
                .SequenceEqual(FormatSignature.Mp4Signature))
        {
            return ContainerFormat.MP4;
        }

        // ── AVI: "RIFF" at offset 0 + "AVI " at offset 8 ──
        if (data.Length >= FormatSignature.AviTypeOffset + FormatSignature.AviTypeSignature.Length &&
            data.Slice(FormatSignature.AviRiffOffset, FormatSignature.AviRiffSignature.Length)
                .SequenceEqual(FormatSignature.AviRiffSignature) &&
            data.Slice(FormatSignature.AviTypeOffset, FormatSignature.AviTypeSignature.Length)
                .SequenceEqual(FormatSignature.AviTypeSignature))
        {
            return ContainerFormat.AVI;
        }

        // ── FLV: "FLV" at offset 0 ──
        if (data.Length >= FormatSignature.FlvOffset + FormatSignature.FlvSignature.Length &&
            data.Slice(FormatSignature.FlvOffset, FormatSignature.FlvSignature.Length)
                .SequenceEqual(FormatSignature.FlvSignature))
        {
            return ContainerFormat.FLV;
        }

        // ── MKV / WebM: EBML magic at offset 0 ──
        if (data.Length >= FormatSignature.EbmlOffset + FormatSignature.EbmlSignature.Length &&
            data.Slice(FormatSignature.EbmlOffset, FormatSignature.EbmlSignature.Length)
                .SequenceEqual(FormatSignature.EbmlSignature))
        {
            return ParseEbmlDocType(data);
        }

        // ── MPEG-TS: sync byte 0x47 every 188 bytes ──
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
    /// <b>V2-11 L5</b>：扫描探测窗口内所有偏移（不仅是 offset 0），
    /// 以识别从非零偏移开始的录制文件（V1 仅检测 offset 0，存在漏报）。
    /// 为降低误报，要求从某偏移起连续至少 3 个同步字节（间隔 188 字节），与任务规格一致。
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

            // 从本偏移起，校验后续 sync byte（间隔 packetSize），连续至少 3 个命中降低误报（对齐任务规格）
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
