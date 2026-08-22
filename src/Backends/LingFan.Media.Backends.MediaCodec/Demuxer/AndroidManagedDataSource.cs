using Android.Media;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// 桥接 <see cref="IMediaStream"/> → 托管 <see cref="MediaDataSource"/>（API 23+），供
/// <see cref="MediaExtractor.SetDataSource(MediaDataSource)"/> 消费无地址流（<c>Location == null</c> 的内存/透传流）。
/// </summary>
/// <remarks>
/// <para>相比手写的 NDK <c>AMediaDataSource</c>（<c>[UnmanagedCallersOnly]</c> 回调）：
/// 本类型走 net-android 托管 Java 绑定（标准 <c>android.media.MediaDataSource</c>），
/// 由框架经 JNI 回调，规避 Android 12+ CFI 系统库对原始函数指针的 <c>__cfi_check_fail</c> SIGTRAP。
/// 若真机仍遇 SIGTRAP，回退方案为「<c>Location == null</c> 时抛 <see cref="PlatformNotSupportedException"/>」。</para>
/// <para><b>生命周期</b>：<see cref="Close"/> 按 Android 语义仅告知消费方不再需要数据（解除阻塞中的读），
/// <b>不释放</b> 底层 <see cref="IMediaStream"/>——流归 <see cref="AndroidDemuxer"/> 所有，由其释放。</para>
/// <para><b>线程安全</b>：框架可能多线程调用 <see cref="ReadAt"/>，故锁串行化对 <see cref="IMediaStream"/> 的访问。</para>
/// </remarks>
internal sealed class AndroidManagedDataSource : MediaDataSource
{
    private readonly IMediaStream _stream;
    private readonly object _gate = new();

    /// <summary>用指定媒体流构造数据源桥。</summary>
    public AndroidManagedDataSource(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>从绝对定位 <paramref name="position"/> 读最多 <paramref name="size"/> 字节到
    /// <paramref name="buffer"/> 的 <paramref name="offset"/> 起。</summary>
    /// <returns>读入字节数（&gt;0）；越界/EOF 返回 -1；<paramref name="size"/> 为 0 返回 0。</returns>
    public override int ReadAt(long position, byte[]? buffer, int offset, int size)
    {
        if (buffer is null || size == 0) return 0;
        try
        {
            lock (_gate)
            {
                if (_stream.Position != position)
                    _stream.Seek(position, SeekOrigin.Begin);

                int total = 0;
                while (total < size)
                {
                    int r = _stream.Read(buffer.AsSpan(offset + total, size - total));
                    if (r == 0) break; // 底层 EOF
                    total += r;
                }
                // size>0 却读不到任何字节 = 已到流末尾，按契约返回 -1。
                return total > 0 ? total : -1;
            }
        }
        catch (Exception)
        {
            // 任何异常转为 -1（错误），绝不穿透到原生读取栈。
            return -1;
        }
    }

    /// <summary>流长度（<see cref="MediaDataSource"/> 抽象属性）；未知返回 -1（框架按未知大小处理）。</summary>
    public override long Size => _stream.Length;

    /// <summary>仅通知数据源"即将不再需要"，不释放流（流归 demuxer 管理）。</summary>
    public override void Close()
    {
        // 预留：若未来需标记"流已关闭"以快速失败后续读，可在此置位（当前无需）。
    }
}