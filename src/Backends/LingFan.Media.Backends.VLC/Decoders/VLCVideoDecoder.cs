namespace LingFan.Media.Backends.VLC.Decoders;

/// <summary>
/// <see cref="IVideoDecoder"/> 的 VLC 直通实现。
/// </summary>
/// <remarks>
/// <para><b>直通解码器</b>：VLC 后端由 VLCDemuxer 通过 VLC 内部管线完成解封装+解码，
/// MediaPacket 携带的已是解码后的视频帧数据（BGRA32 像素）。
/// 本解码器仅将 packet 数据包装为 <see cref="VideoFrame"/>，不做实际解码。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>（无 I/O）。</item>
/// <item><see cref="DecodeAsync"/>：热路径，同步完成，返回 <see cref="ValueTask{TResult}"/>。</item>
/// <item><see cref="FlushAsync"/>：热路径，返回 null（无缓冲帧）。</item>
/// <item><see cref="Reset"/>：同步，无操作。</item>
/// </list>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
internal sealed class VLCVideoDecoder : IVideoDecoder
{
    private readonly ILogger<VLCVideoDecoder> _logger;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="VLCVideoDecoder"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    public VLCVideoDecoder(ILogger<VLCVideoDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec { get; private set; }

    /// <inheritdoc/>
    /// <remarks>VLC 内部处理硬件解码，直通解码器报告 false（硬件状态由 VLC 管理）。</remarks>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    /// <remarks>参数化配置：记录编解码器信息。非生命周期方法。</remarks>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        Codec = codec;
        _logger.LogDebug("VLC 视频直通解码器初始化: {Codec}", codec);
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
    /// 热路径：将 packet 数据（BGRA32 像素）包装为 <see cref="VideoFrame"/>。
    /// 假设 VLCDemuxer 交付的 BGRA32 数据宽度等于 packet 原始宽度（通过 stride 推算）。
    /// </remarks>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Data.Length == 0)
            return new ValueTask<VideoFrame?>((VideoFrame?)null);

        // VLCDemuxer 交付 BGRA32 数据，stride = width * 4
        // 从数据长度推算帧尺寸（假设 stride = width * 4, height = dataLen / stride）
        int stride = 0;
        int width = 0;
        int height = 0;

        // 尝试从数据长度推算尺寸：BGRA32 每像素 4 字节
        // dataLen = width * height * 4
        int dataLen = packet.Data.Length;
        if (dataLen > 0 && dataLen % 4 == 0)
        {
            int pixels = dataLen / 4;
            // 常见分辨率推算
            if (pixels == 1920 * 1080) { width = 1920; height = 1080; }
            else if (pixels == 1280 * 720) { width = 1280; height = 720; }
            else if (pixels == 3840 * 2160) { width = 3840; height = 2160; }
            else if (pixels == 640 * 480) { width = 640; height = 480; }
            else
            {
                // 无法确定尺寸，使用平方根近似
                int side = (int)Math.Sqrt(pixels);
                width = side;
                height = pixels / side;
                if (width * height != pixels)
                {
                    // 无法确定，跳过此帧
                    return new ValueTask<VideoFrame?>((VideoFrame?)null);
                }
            }
            stride = width * 4;
        }

        if (width <= 0 || height <= 0)
            return new ValueTask<VideoFrame?>((VideoFrame?)null);

        // 创建 SoftwareFrameResource（拷贝数据，因为 packet 会被 Dispose）
        var resource = new SoftwareFrameResource(width, height, PixelFormat.BGRA32, packet.Data.ToArray().AsMemory());

        var frame = new VideoFrame(
            width, height, PixelFormat.BGRA32,
            resource, packet.Timestamp, packet.Duration, packet.KeyFrame);

        return new ValueTask<VideoFrame?>(frame);
    }

    /// <inheritdoc/>
    /// <remarks>热路径：直通解码器无缓冲帧，返回 null。</remarks>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        return new ValueTask<VideoFrame?>((VideoFrame?)null);
    }

    /// <inheritdoc/>
    /// <remarks>直通解码器无状态需重置。</remarks>
    public void Reset()
    {
        // 无操作
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：无异步资源，委托 Dispose + CompletedTask。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
