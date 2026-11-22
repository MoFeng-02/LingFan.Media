using Android.Media;
using Java.Nio;
using System.Buffers;

namespace LingFan.Media.Backends.MediaCodec.Demuxer;

/// <summary>
/// 读包分部：采样读取核心、复用缓冲管理与 ArrayPool 归还者。
/// </summary>
internal sealed partial class AndroidDemuxer
{
    // 解封装热路径复用缓冲（避免每包分配）；_maxInputSize 在 Open 时按轨道 max-input-size 上调。
    // 复用经 Java.Nio.ByteBuffer.Wrap 包装的视图；缓冲容量不足时增长并重 Wrap。
    private byte[] _readScratch = Array.Empty<byte>();
    private ByteBuffer? _readBuf;
    private int _maxInputSize = 1 << 16; // 64 KiB 下限

    // 诊断节流：每 64 包打一条读包日志
    private int _packetCounter;
    private const int PacketLogInterval = 64;

    // AOSP MediaExtractor SampleFlags 位：1=SYNC(关键帧)、2=ENCRYPTED（不支持 DRM 即跳过）。
    private const int SampleFlagSync = 1;
    private const int SampleFlagEncrypted = 2;

    /// <summary>ReadPacketAsync 的同步核心：读取当前采样（按 PTS 已自动交错），推进后返回。</summary>
    private MediaPacket? ReadPacketCore()
    {
        var ext = _extractor!;
        while (true)
        {
            // EOF 判定：SampleTrackIndex < 0（AOSP 文档在 EOF 返回 -1），比 flags 可靠（flags 在流尾返回
            // 0xFFFFFFFF 与 SAMPLE_FLAG_* 重叠）。readSampleData <= 0 为冗余 EOF 保护。
            int trackIdx = ext.SampleTrackIndex;
            if (trackIdx < 0) return null;

            int flags = (int)ext.SampleFlags; // SampleFlags 为枚举，显式转 int 再按位判定
            long ptsUs = ext.SampleTime;

            EnsureScratchBuffered();
            _readBuf!.Clear();
            int n = ext.ReadSampleData(_readBuf, 0);
            if (n <= 0) return null; // 无更多采样（EOF）

            if (n >= _readScratch.Length)
            {
                // 缓冲恰好填满：采样可能更大，增长后重读（ReadSampleData 不推进游标）
                GrowScratch();
                EnsureScratchBuffered();
                _readBuf!.Clear();
                n = ext.ReadSampleData(_readBuf, 0);
                if (n <= 0) return null;
            }

            // 推进到下一采样（必须在当前采样读取完成后调用）
            ext.Advance();

            if (n == 0) continue; // 空采样（非 EOF）：跳过，不产出空包

            // 关键：ByteBuffer.Wrap(托管数组) 在 Java 侧是 marshal 副本——ReadSampleData 写入的是
            // Java 侧副本数组，绝不回写 _readScratch！必须用 Get()（Java→C# 方向）把字节取回。
            // （此前误用 Array.Copy(_readScratch,…) 拷出的是从未写入的托管数组 → 喂给解码器全零字节，
            //  导致 video/audio dequeue 恒 TRY_AGAIN、0 产出。）
            // 每包数据经 ArrayPool 租借（grow-only，消除每包 new 的 GC 压力）；packet Dispose 时由
            // RentedBufferOwner 归还池，避免跨包复用导致数据串号（Data 持有数组引用）。
            byte[] data = ArrayPool<byte>.Shared.Rent(n);
            _readBuf.Rewind(); // position=0（Position 在此绑定为方法组，用 Rewind 等价）
            _readBuf.Get(data, 0, n);

            bool key = (flags & SampleFlagSync) != 0;
            var ts = ptsUs >= 0 ? TimeSpan.FromTicks(ptsUs * 10) : TimeSpan.Zero;
            var pkt = new MediaPacket(trackIdx, data.AsMemory(0, n), ts, TimeSpan.Zero, key,
                dataOwner: new RentedBufferOwner(data));

            // 诊断节流日志：读包节奏（track/size/pts/key）；首包附前 12 字节 hex（验证取回方向正确、非全零）。
            // Trace 级：解封装线程为实时线程，周期性 Information 写 logcat（双 provider）可达数十 ms，
            // 会周期性阻断包供给 → 音频欠载/解码断粮（卡顿诱因之一），调高日志级别即可查看。
            if ((_packetCounter++ % PacketLogInterval) == 0)
            {
                if (_packetCounter == 1)
                    _logger.LogTrace(
                        "[ANDROID-DEMUX] 读包 track={Track} size={Size} pts={PtsUs}us key={Key} 累计={Total} hex={Hex}",
                        trackIdx, n, ptsUs, key, _packetCounter,
                        Convert.ToHexString(data.AsSpan(0, Math.Min(12, n))));
                else
                    _logger.LogTrace(
                        "[ANDROID-DEMUX] 读包 track={Track} size={Size} pts={PtsUs}us key={Key} 累计={Total}",
                        trackIdx, n, ptsUs, key, _packetCounter);
            }

            // 不支持 DRM：跳过加密采样，不假装解码
            if ((flags & SampleFlagEncrypted) != 0)
            {
                _logger.LogDebug("[ANDROID-DEMUX] 跳过加密采样 track={Track}", trackIdx);
                pkt.Dispose();
                continue;
            }

            return pkt;
        }
    }

    /// <summary>确保读取缓冲已实例化并 Wrap 到位。</summary>
    private void EnsureScratchBuffered()
    {
        if (_readBuf is not null) return;
        if (_readScratch.Length < _maxInputSize)
            _readScratch = new byte[Math.Min(_maxInputSize, 64 << 20)]; // 上限 64 MiB
        _readBuf = ByteBuffer.Wrap(_readScratch);
    }

    /// <summary>读取缓冲增长（约 1.5x，上限 64 MiB）。</summary>
    private void GrowScratch()
    {
        int next = (int)Math.Min((long)_readScratch.Length * 3 / 2, 64L << 20);
        if (next <= _readScratch.Length) return; // 已达上限
        _readScratch = new byte[next];
        _readBuf = null; // 触发重 Wrap
    }

    /// <summary>ArrayPool 租借缓冲的归还者：MediaPacket.Dispose 经 dataOwner 触发，把缓冲归还 Shared 池，
    /// 消除每包 new 的 GC 压力，同时因 Data 持有数组引用而避免跨包复用导致的数据串号。</summary>
    private sealed class RentedBufferOwner : IDisposable
    {
        private byte[]? _buffer;
        public RentedBufferOwner(byte[] buffer) => _buffer = buffer;
        public void Dispose()
        {
            var buf = _buffer;
            _buffer = null;
            if (buf is not null) ArrayPool<byte>.Shared.Return(buf);
        }
    }
}