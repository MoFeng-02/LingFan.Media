using System.Runtime.InteropServices;
using Android.Media;
using Java.Nio;
// 本后端命名空间段为 ...MediaCodec，会遮蔽类型 Android.Media.MediaCodec → 用不撞名的别名。
using AndroidMediaCodec = Android.Media.MediaCodec;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 输入喂入分部：待喂队列 → 解码器输入槽（含排空后补喂），绝不截断/静默丢包。
/// </summary>
internal sealed partial class AndroidVideoDecoder
{
    private readonly Queue<MediaPacket> _pendingInput = new();

    private int _inputQueued;              // 实际入队的输入缓冲数（vs 仅收包）
    private long _inputDequeueBlocked;     // dequeueInputBuffer 返回 -1 的次数（输入槽满）
    private long _inputBufferNull;         // GetInputBuffer 返回 null 的次数（异常，包保留重试）
    private long _inputTruncated;          // 输入槽装不下整包而被跳过的包数（绝不截断喂入）
    private long _postDrainFed;            // 「排空后补喂」实际入队的包数（>0 证明补喂路径生效）
    private long _postDrainCalls;          // 「排空后补喂」的调用次数

    /// <summary>尽可能把待喂入包拷入解码器输入槽。</summary>
    private void FeedInput()
    {
        while (_pendingInput.Count > 0)
        {
            int idx = _codec!.DequeueInputBuffer(0);
            if (idx < 0)
            {
                // 输入槽暂满：保留包待下次。诊断判定「未入队」而非常见的「入队不出帧」。
                _inputDequeueBlocked++;
                if (_inputDequeueBlocked == 1 || (_inputDequeueBlocked % 256) == 0)
                    _logger.LogWarning("[ANDROID-VID] 输入槽满，喂入被阻（pending={Pending}, 累计阻 {Blk}）, dequeueOut={Out}",
                        _pendingInput.Count, _inputDequeueBlocked, _drainDequeued);
                break;
            }

            ByteBuffer? buf = _codec.GetInputBuffer(idx);
            if (buf is null)
            {
                _inputBufferNull++;
                if (_inputBufferNull <= 8)
                    _logger.LogWarning(
                        "[ANDROID-VID] GetInputBuffer({Idx}) 返回 null（共 {N} 次），保留该包待下次重试（绝不静默丢包）",
                        idx, _inputBufferNull);
                break;
            }

            // 先 Peek 不 Dequeue：只有确认能安全喂入才出队，避免任何分支静默丢包
            // （旧实现 `if (buf is null) continue;` 会把已出队的包在 finally 里 Dispose 掉，
            //  包就此消失且无任何日志 —— 末段丢帧的隐藏来源之一）。
            var pkt = _pendingInput.Peek();

            // 装不下就必须整包跳过、绝不能截断喂入：截断的 H.264 NAL 会让解码器产出
            // 半帧/错帧，正是「花屏」的直接来源。宁可丢这一帧，也不能污染后续帧的参考。
            if (buf.Remaining() < pkt.Data.Length)
            {
                _inputTruncated++;
                if (_inputTruncated <= 8)
                    _logger.LogWarning(
                        "[ANDROID-VID] 输入 buffer 容量({Cap})小于包大小({Len})，跳过整包（绝不截断喂入，避免解码错帧）",
                        buf.Remaining(), pkt.Data.Length);
                _pendingInput.Dequeue();
                pkt.Dispose();
                continue;
            }

            _pendingInput.Dequeue();
            try
            {
                int len = pkt.Data.Length;
                var mem = pkt.Data;
                if (MemoryMarshal.TryGetArray(mem, out ArraySegment<byte> seg) && seg.Array is not null)
                    buf.Put(seg.Array, seg.Offset, len);
                else
                    buf.Put(mem.ToArray(), 0, len);

                long ptsUs = pkt.Timestamp.Ticks > 0 ? pkt.Timestamp.Ticks / 10 : 0;
                _codec.QueueInputBuffer(idx, 0, len, ptsUs, (MediaCodecBufferFlags)0);
                _inputQueued++;
            }
            finally
            {
                pkt.Dispose();
            }
        }
    }

    /// <summary>排空输出后立即补喂输入。</summary>
    /// <remarks>
    /// <b>为什么必须有这一步</b>：MediaCodec 的输入槽位只有在
    /// 输出缓冲被 <c>ReleaseOutputBuffer</c> 归还后才会腾出来。而调用链原本是
    /// 「收包 → <see cref="FeedInput"/> → <see cref="ReadOutput"/>」，即<b>先喂、后排空</b>：
    /// 喂的时候槽位还是满的（必失败），等排空把槽位腾出来了，<b>却再没有人补喂</b>，
    /// 只能干等下一个包到来（30 包/秒）才想起喂。
    /// 更糟的是 <see cref="ReadOutput"/> 与 <see cref="DrainOutput"/> 在
    /// <c>_pendingFrames.Count &gt; 0</c> 时直接返回队首、<b>根本不走 dequeue</b>，
    /// 于是约 3/4 的调用既没排空也没补喂。
    /// 后果：解码器长期半饥饿 —— 开播期帧率明显低于目标、帧队列一度归零。
    /// 画面表现即「开播十几秒糊/拖影、之后完全正常」——<b>不是像素错，是喂不饱</b>。
    /// </remarks>
    private void FeedAfterDrain()
    {
        _postDrainCalls++;
        int before = _inputQueued;
        FeedInput();
        _postDrainFed += _inputQueued - before;
    }
}