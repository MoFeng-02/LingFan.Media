using System.Buffers;
using LingFan.Media.Abstractions;

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
/// <para>本类同时实现 <see cref="IFormatDetector"/> 契约（DetectProfile / DetectProfileAsync），
/// 供高层中间件（如回退调度器）经依赖倒置仅依赖契约、不引用具体实现。</para>
/// <para><b>分部划分</b>：Signatures 分部管容器签名匹配（魔数 / MPEG-TS / EBML）；
/// Profile 分部管容器+编码轻量探测与编码标识串搜索。本文件为容器探测入口。</para>
/// </remarks>
public partial class FormatDetector : IFormatDetector
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
}