using LingFan.Media.Backends.MediaCodec.Interop;

namespace LingFan.Media.Backends.MediaCodec.Wrappers;

/// <summary>
/// NDK <c>AMediaExtractor</c> 的托管包装（解封装器）。
/// </summary>
/// <remarks>
/// <para>持有原生 <c>AMediaExtractor*</c> 并在 <see cref="Dispose"/> 中释放。</para>
/// <para>数据源选择：</para>
/// <list type="bullet">
/// <item><see cref="SetDataSource(string)"/> 接收文件地址或 URL（<b>API 21+</b>），由 NDK 原生读取；</item>
/// <item><see cref="SetDataSource(AndroidDataSource)"/> 桥接 <see cref="IMediaStream"/>（<b>API 28+</b>），
/// 低于 28 的运行时调用会抛 <see cref="EntryPointNotFoundException"/>，由 demuxer 捕获后降级。</item>
/// </list>
/// <para>多轨交织：选中多条轨道后，<c>readSampleData</c> + <c>advance</c> 按 PTS 自动交错返回各轨采样，
/// 调用方按 <c>SampleTrackIndex</c> 路由即可，无需自行排序。</para>
/// </remarks>
internal sealed class AndroidMediaExtractor : IDisposable
{
    private nint _native;

    /// <summary>新建解封装器；失败抛 <see cref="OutOfMemoryException"/>。</summary>
    public AndroidMediaExtractor()
    {
        _native = MediaNdk.AMediaExtractor_new();
        if (_native == nint.Zero)
            throw new OutOfMemoryException("[ANDROID-EXT] AMediaExtractor_new 返回 null");
    }

    /// <summary>设置数据源为文件地址或 URL（API 21+）。</summary>
    public void SetDataSource(string location)
    {
        int status = MediaNdk.AMediaExtractor_setDataSource(_native, location);
        if (status != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-EXT] setDataSource('{location}') 失败: media_status_t={status}");
    }

    /// <summary>设置数据源为自定义 <see cref="AndroidDataSource"/>（API 28+；低版本抛 EntryPointNotFoundException）。</summary>
    public void SetDataSource(AndroidDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        int status = MediaNdk.AMediaExtractor_setDataSourceCustom(_native, dataSource.NativeHandle);
        if (status != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-EXT] setDataSourceCustom 失败: media_status_t={status}");
    }

    /// <summary>轨道数。</summary>
    public nuint TrackCount => MediaNdk.AMediaExtractor_getTrackCount(_native);

    /// <summary>取轨道格式（调用方负责释放返回的 <see cref="AndroidMediaFormat"/>）。</summary>
    public AndroidMediaFormat GetTrackFormat(nuint idx)
    {
        nint fmt = MediaNdk.AMediaExtractor_getTrackFormat(_native, idx);
        if (fmt == nint.Zero)
            throw new InvalidOperationException($"[ANDROID-EXT] getTrackFormat({idx}) 返回 null");
        return new AndroidMediaFormat(fmt);
    }

    /// <summary>取容器级格式；无则返回空格式。</summary>
    public AndroidMediaFormat GetFileFormat()
    {
        nint fmt = MediaNdk.AMediaExtractor_getFileFormat(_native);
        if (fmt == nint.Zero)
            return new AndroidMediaFormat();
        return new AndroidMediaFormat(fmt);
    }

    /// <summary>选中轨道（仅被选中轨道参与 readSampleData）。</summary>
    public void SelectTrack(nuint idx)
    {
        int s = MediaNdk.AMediaExtractor_selectTrack(_native, idx);
        if (s != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-EXT] selectTrack({idx}) 失败: media_status_t={s}");
    }

    /// <summary>取消选中轨道。</summary>
    public void UnselectTrack(nuint idx)
    {
        int s = MediaNdk.AMediaExtractor_unselectTrack(_native, idx);
        if (s != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-EXT] unselectTrack({idx}) 失败: media_status_t={s}");
    }

    /// <summary>
    /// 读取当前采样到 <paramref name="buffer"/>（容量上限）。返回写入字节数；&lt; 0 表示无更多采样（流结束）。
    /// </summary>
    public unsafe int ReadSampleData(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        fixed (byte* p = buffer)
        {
            nint r = MediaNdk.AMediaExtractor_readSampleData(_native, (nint)p, (nuint)buffer.Length);
            return (int)r; // ssize_t
        }
    }

    /// <summary>当前采样标志（<c>SAMPLE_FLAG_*</c>：1=SYNC / 2=ENCRYPTED）；仅当存在当前采样时有意义。</summary>
    public uint SampleFlags => MediaNdk.AMediaExtractor_getSampleFlags(_native);

    /// <summary>当前采样所属轨道索引；无更多采样（EOF）返回 -1。这是解封装主循环判尾的权威依据。</summary>
    public int SampleTrackIndex => MediaNdk.AMediaExtractor_getSampleTrackIndex(_native);

    /// <summary>当前采样 PTS（微秒）；无更多采样返回 -1。</summary>
    public long SampleTimeUs => MediaNdk.AMediaExtractor_getSampleTime(_native);

    /// <summary>当前采样字节数（ssize_t，<b>API 28+</b>）；无更多采样返回 -1。</summary>
    /// <remarks>本后端解封装热路径为兼容 API 21+ 文件路径<b>不使用本方法</b>（改用可增长读取缓冲规避
    /// 低版本 <c>EntryPointNotFoundException</c>）；保留以作 API 完整性。</remarks>
    public nint SampleSize => MediaNdk.AMediaExtractor_getSampleSize(_native);

    /// <summary>前进到下一采样；返回 false 表示已到流末尾。</summary>
    public bool Advance() => MediaNdk.AMediaExtractor_advance(_native) != 0;

    /// <summary>定位（<paramref name="mode"/> 取 AMEDIAEXTRACTOR_SEEK_*）。</summary>
    public void SeekTo(long posUs, int mode)
    {
        int s = MediaNdk.AMediaExtractor_seekTo(_native, posUs, mode);
        if (s != AndroidMediaConstants.AMEDIA_OK)
            throw new InvalidOperationException($"[ANDROID-EXT] seekTo 失败: media_status_t={s}");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_native == nint.Zero) return;
        MediaNdk.AMediaExtractor_delete(_native);
        _native = nint.Zero;
    }
}
