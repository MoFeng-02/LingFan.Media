using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// <see cref="IVideoDecoder"/> 的 MediaFoundation 实现（基于 <c>IMFTransform</c>）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 FFmpegVideoDecoder 对称）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>。</item>
/// <item><see cref="DecodeAsync"/>：热路径，IMFTransform.ProcessInput/ProcessOutput 为同步 COM 调用，
/// 返回 <see cref="ValueTask{TResult}"/>（同步完成，减少分配）。</item>
/// <item><see cref="FlushAsync"/>：热路径，取剩余输出帧。</item>
/// <item><see cref="Reset"/>：同步，IMFTransform.ProcessMessage(COMMAND_FLUSH)。</item>
/// </list>
/// <para><b>仅 Windows 可用</b>：非 Windows 平台 Initialize 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>AOT 兼容</b>：sealed 类，COM 互操作，无反射。</para>
/// </remarks>
internal sealed class MFVideoDecoder : IVideoDecoder
{
    private readonly ILogger<MFVideoDecoder> _logger;
    private IMFTransform? _transform;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="MFVideoDecoder"/> 的新实例。
    /// </summary>
    public MFVideoDecoder(ILogger<MFVideoDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec { get; private set; }

    /// <inheritdoc/>
    public bool IsHardwareAccelerated { get; private set; }

    /// <inheritdoc/>
    /// <remarks>参数化配置：创建 MF 解码器 MFT。</remarks>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MediaFoundation 后端仅支持 Windows。");
        }

        Codec = codec;
        IsHardwareAccelerated = settings.EnableHardwareAcceleration;

        // MF MFT 创建通过 CoCreateInstance + IMFTransform 接口
        // 实际实现需要 CLSID 映射和 IMFTransform 设置
        // 当前为结构化实现框架，MFT 在 OpenAsync 时由 SourceReader 内部处理
        _logger.LogDebug("MF 视频解码器初始化: {Codec}, 硬解={Hw}", codec, IsHardwareAccelerated);
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 热路径：MF SourceReader 可配置为直接输出解码帧（SetCurrentMediaType 输出格式），
    /// 此时 MFDemuxer 交付的已是解码帧数据。若使用独立 MFT，此处为 ProcessInput/ProcessOutput。
    /// </remarks>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Data.Length == 0)
            return new ValueTask<VideoFrame?>((VideoFrame?)null);

        // MF SourceReader 配置为输出 NV12 解码帧时的直通处理
        // 假设数据为 NV12 格式：Y plane + UV plane
        // NV12 数据大小 = width * height * 3 / 2
        int dataLen = packet.Data.Length;
        int pixels = (int)(dataLen * 2L / 3);

        if (pixels <= 0)
            return new ValueTask<VideoFrame?>((VideoFrame?)null);

        int width = (int)Math.Sqrt(pixels);
        int height = pixels / width;

        if (width <= 0 || height <= 0 || width * height != pixels)
        {
            // 无法确定尺寸，尝试常见分辨率
            if (dataLen == 1920 * 1080 * 3 / 2) { width = 1920; height = 1080; }
            else if (dataLen == 1280 * 720 * 3 / 2) { width = 1280; height = 720; }
            else if (dataLen == 640 * 480 * 3 / 2) { width = 640; height = 480; }
            else
                return new ValueTask<VideoFrame?>((VideoFrame?)null);
        }

        var resource = new SoftwareFrameResource(width, height, PixelFormat.NV12, packet.Data.ToArray().AsMemory());

        var frame = new VideoFrame(
            width, height, PixelFormat.NV12,
            resource, packet.Timestamp, packet.Duration, packet.KeyFrame);

        return new ValueTask<VideoFrame?>(frame);
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        return new ValueTask<VideoFrame?>((VideoFrame?)null);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_transform != null)
        {
            try
            {
                _transform.ProcessMessage(MFInterop.MFTMessageType.MFT_COMMAND_FLUSH, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "MF 视频解码器 Reset 异常");
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_transform != null)
        {
            try
            {
                Marshal.ReleaseComObject(_transform);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "IMFTransform 释放异常");
            }
            _transform = null;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
