using System.IO;
using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Demuxer;

// FFmpegDemuxer 的 Avio 职责段（部分类，由 FFmpegDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Close/Dispose）在主文件；libavformat 同步 C 调用经 Task.Run 卸载。
internal sealed partial class FFmpegDemuxer
{
    /// <summary>
    /// AVIO 读取回调。同步边界：C 函数指针签名强制同步。
    /// 调用 <see cref="IMediaStream.Read(Span{byte})"/> 同步读取。
    /// </summary>
    /// <remarks>
    /// 运行在 FFmpeg 工作线程（非 UI 线程）。建连已在 <see cref="OpenAsync"/> 的异步路径完成，
    /// 此处仅做已连接流的逐块同步读取——这是 FFmpeg C API 的固有同步边界，非伪异步。
    /// EOF 与错误严格区分：读到 0 字节 = EOF；异常（含网络读超时/连接中断）= AVERROR_EXTERNAL，
    /// 交由上层按错误处理而非当作播放结束。
    /// </remarks>
    private unsafe int ReadPacketCallback(void* opaque, byte* buf, int bufSize)
    {
        // 同步边界：绝不允许托管异常逃逸进原生 ffmpeg（否则进程静默死亡、无托管栈迹）。
        try
        {
            if (_stream == null || _disposed)
                return FF.AVERROR_EOF;

            if (bufSize <= 0)
                return FF.AVERROR_EOF;

            Span<byte> span = new(buf, bufSize);
            int read = _stream.Read(span);

            if (read <= 0)
            {
                // 定位窗口内数据源直接返回 0 与「容器层读到的流末」是两种成因（落点超出可读范围、
                // 或 Range 连接已被对端关闭）。返回值不变（上层仍按流末收尾），
                // 只留下带定位目标的上下文，便于结合上层日志判断读链路是否正常。
                if (_logFirstPacketAfterSeek)
                {
                    _logger.LogWarning(
                        "定位窗口内数据源读取返回 {ReadBytes}：targetTs={TargetTs}，读链路可能已中断或落点不可读",
                        read, _lastSeekTargetTs);
                }

                return FF.AVERROR_EOF;
            }

            return read;
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "AVIO ReadPacketCallback 异常（已吞除以防逃逸进原生 ffmpeg）"); } catch { }
            return FF.AVERROR_EXTERNAL;
        }
    }

    /// <summary>
    /// AVIO 定位回调。同步边界：委托给 <see cref="IMediaStream.Seek"/>。
    /// </summary>
    private unsafe long SeekCallback(void* opaque, long offset, int whence)
    {
        // 同步边界：同上，绝不允许托管异常逃逸进原生 ffmpeg。
        try
        {
            if (_stream == null || _disposed || !_stream.CanSeek)
                return FF.AVERROR_EXTERNAL;

            // AVSEEK_SIZE：查询流大小
            if (whence == FF.AVSEEK_SIZE)
            {
                long len = _stream.Length;
                return len < 0 ? FF.AVERROR_EXTERNAL : len;
            }

            SeekOrigin origin = whence switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => SeekOrigin.Begin
            };

            return _stream.Seek(offset, origin);
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "AVIO SeekCallback 异常（已吞除以防逃逸进原生 ffmpeg）"); } catch { }
            return FF.AVERROR_EXTERNAL;
        }
    }

    /// <summary>清理 AVIO 资源。</summary>
    /// <remarks>
    /// 缓冲以 av_malloc 分配（avio.h 契约），所有权在读期间移交 libavformat（其可能经
    /// av_realloc 替换缓冲），由 avio_closep 内部的 av_freep(&amp;s->buffer) 统一归还——
    /// 调用方不得再释放。勿「置空 buffer + 手动 FreeHGlobal」——ffmpeg 替换过缓冲时
    /// 会泄漏新缓冲，且分配器与契约不符。
    /// </remarks>
    private unsafe void CleanupAVIO()
    {
        if (_avioContext != IntPtr.Zero)
        {
            AVIOContext* avioCtx = (AVIOContext*)_avioContext;
            FF.avio_closep(&avioCtx);
            _avioContext = IntPtr.Zero;
        }

        _avioBuffer = IntPtr.Zero;
    }
}
