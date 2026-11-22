using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

// MFDemuxer 的 Cleanup 职责段（部分类，由 MFDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Dispose）在主文件；IMFSourceReader 同步 COM 调用，COM 线程亲和。
internal sealed partial class MFDemuxer
{
    /// <summary>同步两阶段关闭（供 <see cref="Close"/> / <see cref="Dispose"/> 使用）。</summary>
    private void CloseSync()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        try
        {
            _readerGate.BeginClose();

            // (2) 排空在途原生调用。**此时专用线程仍存活、COM 单元完整**（这正是它必须先于 Shutdown 的原因）。
            if (!_readerGate.WaitDrain(MediaPipelineTimeouts.NativeDrain))
            {
                LeakNativeResources("在途原生调用未在期限内排空", schedulerExited: false);
                return;
            }

            var scheduler = _readerScheduler;
            if (scheduler is null)
            {
                // 从未建立专用线程（Open 之前即关闭）。此时不应存在任何 COM 指针；若存在则说明状态异常，
                // 在未知单元的线程上释放即违反 COM 单元不变量 ⇒ 取安全侧泄漏。
                if (_sourceReader != IntPtr.Zero)
                {
                    LeakNativeResources("存在 COM 指针但专用单元线程已不可用", schedulerExited: true);
                    return;
                }
                ReleaseComObjectsOnOwnerThread(); // 全 no-op，仅为收敛 _mfStartupAcquired
                ReleaseManagedState();
                return;
            }

            // (3) COM 单元不变量：把 Release 投递回创建它的专用单元线程，必须在该线程 CoUninitialize 之前完成。
            if (!scheduler.TryRunOnSchedulerThread(ReleaseComObjectsOnOwnerThread, MediaPipelineTimeouts.NativeDrain))
            {
                LeakNativeResources("无法在专用 COM 单元线程上完成释放", schedulerExited: false);
                return;
            }

            // (4) COM 指针已释放，现在才放行专用线程退出（其 finally 将执行 CoUninitialize）。
            bool schedulerExited = scheduler.Shutdown(MediaPipelineTimeouts.SchedulerJoin);
            if (!schedulerExited)
                _logger.LogWarning("MFDemuxer 专用读取线程未在期限内退出；COM 指针已安全释放，仅线程与队列延迟回收。");

            ReleaseManagedState(); // (5)
        }
        finally { _opened = false; }
    }

    /// <summary>异步两阶段关闭（供 <see cref="DisposeAsync"/> 使用，全程 await，不阻塞调用线程）。</summary>
    /// <remarks>
    /// 调度器等待走 <c>ShutdownAsync</c>（线程退出 TCS + <c>WaitAsync</c>），
    /// 而非 <c>Shutdown</c> 的 <c>Thread.Join</c>——后者会在异步释放链上引入最长 5s 的硬同步阻塞，
    /// 与「真异步方法一路 await 到底、禁止 .Wait()/.Result 式阻塞」的准则直接冲突。
    /// 步骤顺序与 <see cref="CloseSync"/> 逐条同构（COM 单元不变量同样适用）。
    /// </remarks>
    private async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        try
        {
            _readerGate.BeginClose();

            // (2)
            if (!await _readerGate.WaitDrainAsync(MediaPipelineTimeouts.NativeDrain).ConfigureAwait(false))
            {
                LeakNativeResources("在途原生调用未在期限内排空", schedulerExited: false);
                return;
            }

            var scheduler = _readerScheduler;
            if (scheduler is null)
            {
                if (_sourceReader != IntPtr.Zero)
                {
                    LeakNativeResources("存在 COM 指针但专用单元线程已不可用", schedulerExited: true);
                    return;
                }
                ReleaseComObjectsOnOwnerThread();
                ReleaseManagedState();
                return;
            }

            // (3) COM 单元不变量
            if (!await scheduler.TryRunOnSchedulerThreadAsync(
                    ReleaseComObjectsOnOwnerThread, MediaPipelineTimeouts.NativeDrain).ConfigureAwait(false))
            {
                LeakNativeResources("无法在专用 COM 单元线程上完成释放", schedulerExited: false);
                return;
            }

            // (4)
            bool schedulerExited = await scheduler.ShutdownAsync(MediaPipelineTimeouts.SchedulerJoin).ConfigureAwait(false);
            if (!schedulerExited)
                _logger.LogWarning("MFDemuxer 专用读取线程未在期限内退出；COM 指针已安全释放，仅线程与队列延迟回收。");

            ReleaseManagedState(); // (5)
        }
        finally { _opened = false; }
    }

    /// <summary>
    /// 协议步骤(3)：释放原生 COM 资源。<b>必须且只能在创建它们的专用单元线程上执行</b>（COM 单元不变量）。
    /// </summary>
    /// <remarks>
    /// 前置：gate 已排空（独占）——不存在任何其它线程处于闸内，故本方法可独占访问 <c>_sourceReader</c>。
    /// 调用点仅两处，均经 <c>TryRunOnSchedulerThread(Async)</c> 投递或在确认无 COM 指针时内联，不得直接调用。
    /// </remarks>
    private void ReleaseComObjectsOnOwnerThread()
    {
        if (_sourceReader != IntPtr.Zero)
        {
            try
            {
                // 错误链：经 tracer 记录并（严格模式）纳入 UAF/重复释放检测。
                InteropTrace.ReleaseComPtr(_sourceReader, "MFDemuxer:_sourceReader");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "IMFSourceReader 释放异常");
            }
            _sourceReader = IntPtr.Zero;
            _readSample = null; // 同步置空 vtable 委托，漏网调用点得到可诊断 NRE 而非静默 UAF
        }

        // 配对 MFPlatform 引用（仅成功分支执行；LeakNativeResources 绝不递减）。
        // 与 OpenCore 中的 Startup 同线程同单元，且此刻 _sourceReader 已释放，递减不会踩到任何在途调用。
        if (_mfStartupAcquired)
        {
            _mfStartupAcquired = false;
            try { MFPlatform.Shutdown(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "MFShutdown 配对调用异常（忽略，不影响释放流程）");
            }
        }
    }

    /// <summary>协议步骤(5)：纯托管收尾，无单元亲和要求，可在任意线程执行。</summary>
    private void ReleaseManagedState()
    {
        // 释放尚未投递的 lookahead 数据包（MediaPacket 独立拥有托管副本，Dispose 兜底，防泄漏）
        foreach (var pkt in _pendingPackets.Values)
            pkt.Dispose();
        _pendingPackets.Clear();
        _exhaustedStreams.Clear();

        // Shutdown 返回 false（判定时线程仍存活）的路径不会 Dispose 队列；丢弃引用前用零超时
        // 再试一次，让「线程随后才退出」的迟到情形也能回收 BlockingCollection。零超时不阻塞、幂等、不碰 COM。
        _readerScheduler?.Shutdown(TimeSpan.Zero);
        _readerScheduler = null;
        _readerFactory = null;
    }

    /// <summary>协议失败分支：一切不动，有意泄漏并告警。绝不 Release / 置 NULL / 清容器 / 关调度器。</summary>
    private void LeakNativeResources(string reason, bool schedulerExited)
    {
        if (_leakedOnClose) return;
        _leakedOnClose = true;
        _logger.LogError("MFDemuxer 安全关闭失败（{Reason}；schedulerExited={SchedulerExited}）。" +
            "已【有意保留】IMFSourceReader，避免因释放导致原生堆损坏（COR_E_EXECUTIONENGINE）。" +
            "泄漏有界且可诊断；进程自杀不可接受。", reason, schedulerExited);
        // 不 Release、不置 _sourceReader=Zero、不清 _pendingPackets、不置 _readSample=null。
        // **也不递减 MFPlatform 引用**（_mfStartupAcquired 保持 true）。在途的原生 ReadSample
        // 依赖 MF 平台仍然存活；此处若配对 Shutdown，可能让计数归 0 触发真 MFShutdown，把平台从在途调用
        // 脚下抽走 ⇒ 恰好制造本协议要避免的原生堆损坏。平台引用与 COM 指针同进退：要么一起释放，要么一起泄漏。
        //
        // COM 单元不变量推论：**也绝不 Shutdown 调度器**。让专用线程退出即让它 CoUninitialize，
        // 会拆掉泄漏指针所属的 COM 单元、卸载其 in-proc server——泄漏本是为保住指针可用性，
        // 拆单元等于把保护对象连根拔起。线程为后台线程，进程退出时由 OS 回收，泄漏有界。
    }
}
