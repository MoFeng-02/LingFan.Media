using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Concurrency;

/// <summary>
/// 单线程 <see cref="TaskScheduler"/>：所有入队任务在一条持久后台线程上顺序执行。
/// </summary>
/// <remarks>
/// <para>用途：把 Media Foundation <c>IMFSourceReader</c> 的全部 COM 调用（<c>ReadSample</c> / <c>SetCurrentPosition</c> /
/// <c>GetNativeMediaType</c> 等）钉在同一条线程上。MF 的同步 <c>ReadSample</c> 会缓存与调用线程相关的内部状态，
/// 若每次调用落在不同的线程池线程（<c>Task.Run</c> 默认行为），可能触发原生堆损坏
/// （<c>COR_E_EXECUTIONENGINE</c> / <c>0x80131506</c>，非确定性、常在若干次成功读取后才爆发）。</para>
/// <para>后台线程为 MTA（首次 COM 调用由 CLR 自动初始化），与 MF SourceReader 兼容。</para>
/// <para><b>生命周期</b>：<see cref="Shutdown"/> / <see cref="ShutdownAsync"/>（<see cref="Dispose"/> 委托前者）
/// 调用 <see cref="BlockingCollection{T}.CompleteAdding"/> 后等待线程排空队列退出（可配置超时）。
/// 本类<b>不再依赖</b>调用方守约「Dispose 时无在途任务」——
/// 真正的释放安全由 <c>NativeCallGate</c> 保证：即便本调度器线程仍卡在原生调用内，
/// 调用方也应据 <c>NativeCallGate</c> 的排空结果（而非本类返回值单独）决定是否释放 COM 指针。</para>
/// <para><b>同步/异步双支持</b>：等待线程退出是真实的等待，故 <see cref="Shutdown"/>（<see cref="Thread.Join(TimeSpan)"/>）与
/// <see cref="ShutdownAsync"/>（线程退出 TCS + <c>WaitAsync</c>，不占用调用线程）成对提供；
/// <c>DisposeAsync</c> 路径必须用后者，否则会在异步释放链上引入最长数秒的同步阻塞。</para>
/// <para><b>AOT 兼容</b>：sealed 类，纯 BCL 类型，无反射。</para>
/// </remarks>
internal sealed class SingleThreadTaskScheduler : TaskScheduler, IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<Task> _tasks = new(new ConcurrentQueue<Task>());

    // 线程退出信号：供 ShutdownAsync 无阻塞等待。RunContinuationsAsynchronously 防止续体在
    // 后台线程的 finally 内联执行（会拖住线程真正退出，进而反过来让 IsAlive 判定失真）。
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0=未关闭，1=已发起关闭。Interlocked 保证 Shutdown/ShutdownAsync/Dispose 并发时 CompleteAdding 只执行一次。
    private int _shutdownStarted;

    // 0=队列未释放，1=已释放。仅在确认线程退出后置位，Interlocked 保证只 Dispose 一次。
    private int _queueDisposed;

    // 供 TryRunOnSchedulerThread* 投递收尾动作复用（关闭路径低频，构造一次即可）。
    private readonly TaskFactory _ownerFactory;

    public SingleThreadTaskScheduler(string name)
    {
        _ownerFactory = new TaskFactory(this);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
    }

    /// <summary>当前线程是否即本调度器的专用线程（用于避免自投递死锁）。</summary>
    public bool IsOnSchedulerThread => ReferenceEquals(Thread.CurrentThread, _thread);

    private void Loop()
    {
        // ⚠️ 显式初始化 COM 单元（MTA）——根因修复（2026-07-31）。
        // 本线程由 new Thread 手动创建，CLR 不会自动 CoInitializeEx（仅 RCW 互操作路径才自动初始化）。
        // 该线程承载 MFDemuxer 对 IMFSourceReader 的全部原始 vtable P/Invoke 调用；
        // 裸线程（无 COM 单元）调用 MF 原生 COM 会间歇踩坏原生堆 → COR_E_EXECUTIONENGINE / 0x80131506
        // （非确定性、常在若干次成功读后才爆发）——即 MF e2e 冷启动 flaky 崩溃的根因。
        // 选用 MTA：无需消息泵，与裸 foreach 循环兼容（STA 需 pump，不可用于此）。
        int coInitHr = MFInterop.CoInitializeEx(IntPtr.Zero, MFInterop.COINIT_MULTITHREADED);
        bool coInitialized = coInitHr >= 0; // S_OK 或 S_FALSE 均视为本线程成功初始化
        try
        {
            // GetConsumingEnumerable 在 CompleteAdding 且队列排空后自动结束，线程随之退出。
            foreach (var task in _tasks.GetConsumingEnumerable())
                TryExecuteTask(task);
        }
        finally
        {
            // 仅当本线程成功初始化后才反初始化；若返回 RPC_E_CHANGED_MODE（已被他人初始化）则不配对 CoUninitialize。
            //
            // 🔴 单元亲和铁律（2026-08-01 二次根因修复，勿回退）：
            // CoUninitialize 会关闭本线程的 COM 库、对本线程加载过的 in-proc server 逐个 DllCanUnloadNow 卸载，
            // 并在本线程是最后一个 MTA 成员时拆除整个 MTA。因此**在本线程上创建的一切 COM 对象，
            // 其最终 Release 必须先于此处执行**——否则那次 Release 会跳进已卸载/已失效的 vtable，
            // 造成原生访问违例，CLR 报 `Fatal error. Internal CLR error. (0x80131506)`（确定性，非 flaky）。
            // 调用方通过 TryRunOnSchedulerThread(Async) 把 Marshal.Release 投递回本线程完成，
            // 之后才允许发起 Shutdown 让本线程退出（见 MFDemuxer 两阶段关闭协议步骤③）。
            if (coInitialized)
                MFInterop.CoUninitialize();

            // 线程即将退出：置位退出信号（ShutdownAsync 据此无阻塞返回）。
            _exited.TrySetResult();
        }
    }

    // 关闭竞态（CompleteAdding 后入队）会抛 InvalidOperationException；TPL 将其包装为 TaskSchedulerException，
    // 由 MFDemuxer.ReadPacketAsync 在 StartNew 站点捕获并走 EOS 收尾（见修复方案 D4）。此处不吞掉该异常——
    // 若吞掉，任务既不运行也不完成，反而令上游 await 永久挂死。
    protected override void QueueTask(Task task) => _tasks.Add(task);

    // 永不在调用方线程内联执行——所有任务必须在专用线程上跑，保证 COM 对象单线程亲和。
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        // 仅供调试器枚举。队列在线程确认退出后会被 Dispose，此处容忍 ODE 返回空集。
        try { return _tasks.ToArray(); }
        catch (ObjectDisposedException) { return []; }
    }

    /// <summary>
    /// 发起关闭：<see cref="BlockingCollection{T}.CompleteAdding"/> 只执行一次（Interlocked 幂等）。
    /// </summary>
    private void BeginShutdown()
    {
        // 并发的 Shutdown / ShutdownAsync / Dispose 只允许一次 CompleteAdding；
        // 后到者直接进入等待阶段，不重复操作队列。
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        // 完成入队、允许线程排空所有待执行任务后退出（不取消在途任务，避免调用方 await 悬挂）。
        try { _tasks.CompleteAdding(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        { /* 已 CompleteAdding 或队列已释放，说明线程早已退出 */ }
    }

    /// <summary>
    /// 释放队列：仅在<b>确认后台线程已退出 <c>foreach</c> 循环</b>后调用，Interlocked 保证只释放一次。
    /// </summary>
    private void DisposeQueue()
    {
        if (Interlocked.Exchange(ref _queueDisposed, 1) != 0)
            return;
        _tasks.Dispose();
    }

    /// <summary>
    /// 在本调度器的专用线程上执行一个收尾动作并等待其完成（同步）。
    /// </summary>
    /// <param name="action">须在本线程 COM 单元内执行的动作（典型：<c>Marshal.Release</c> 释放本线程创建的 COM 指针）。</param>
    /// <param name="timeout">等待动作执行完成的上限。</param>
    /// <returns><see langword="true"/>=动作已在专用线程上执行完毕；<see langword="false"/>=无法保证已执行（关闭已发起 / 队列已关 / 超时），调用方应走泄漏路径。</returns>
    /// <remarks>
    /// <para>存在意义见 <see cref="Loop"/> 中「单元亲和铁律」注释：本线程 <c>CoUninitialize</c> 之后再释放它创建的 COM 对象
    /// 会导致确定性 <c>0x80131506</c>。故释放动作必须借本方法回到专用线程执行，且必须在 <see cref="Shutdown"/> 之前调用。</para>
    /// <para><b>自调用安全</b>：若调用方本身就跑在专用线程上（如 <c>OpenCore</c> 内失败回滚），直接内联执行，绝不自投递死锁。</para>
    /// <para><b>关闭后拒绝</b>：一旦 <see cref="BeginShutdown"/> 已发起，队列可能已 <c>CompleteAdding</c>、线程可能已退出并 <c>CoUninitialize</c>，
    /// 此时无法再保证单元有效，一律返回 <see langword="false"/> 让调用方选择安全侧（泄漏）而非危险侧（跨单元释放）。</para>
    /// <para>返回 false 且动作事实上仍排在队列中时，动作会在稍后由专用线程执行——那只是「延迟释放」，
    /// 发生在调用方已判定泄漏、不再触碰指针之后，因而无害。</para>
    /// </remarks>
    public bool TryRunOnSchedulerThread(Action action, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsOnSchedulerThread) { action(); return true; }
        if (Volatile.Read(ref _shutdownStarted) != 0 || !_thread.IsAlive) return false;

        Task task;
        try { task = _ownerFactory.StartNew(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskSchedulerException)
        {
            return false; // 与关闭竞态：队列已 CompleteAdding / 已释放
        }

        // 同步等待：TryExecuteTaskInline 恒 false，故 Wait 绝不会把动作内联到本线程（那会破坏单元亲和）。
        try { return task.Wait(timeout) && task.IsCompletedSuccessfully; }
        catch (AggregateException) { return false; } // 动作自身抛出：视为未安全完成，走泄漏路径
    }

    /// <summary>
    /// 在本调度器的专用线程上执行一个收尾动作并等待其完成（异步，不阻塞调用线程）。
    /// </summary>
    /// <inheritdoc cref="TryRunOnSchedulerThread(Action, TimeSpan)" path="/param|/returns|/remarks"/>
    public ValueTask<bool> TryRunOnSchedulerThreadAsync(Action action, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsOnSchedulerThread) { action(); return new ValueTask<bool>(true); }
        if (Volatile.Read(ref _shutdownStarted) != 0 || !_thread.IsAlive) return new ValueTask<bool>(false);

        Task task;
        try { task = _ownerFactory.StartNew(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskSchedulerException)
        {
            return new ValueTask<bool>(false);
        }

        if (timeout <= TimeSpan.Zero) return new ValueTask<bool>(false);
        return AwaitOwnerActionCore(task, timeout);
    }

    private static async ValueTask<bool> AwaitOwnerActionCore(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception) // TimeoutException（未完成）或动作自身异常：均判定未安全完成
        {
            return false;
        }
    }

    /// <summary>
    /// 优雅关闭（同步）：完成入队后等待后台线程排空队列退出。
    /// </summary>
    /// <param name="timeout">等待线程退出的最长时间。</param>
    /// <returns>线程是否已退出（true=已退出；false=超时，线程仍可能卡在原生调用内）。</returns>
    /// <remarks>
    /// <para>调用方<b>必须</b>据返回值结合 <c>NativeCallGate</c> 的排空结果决定是否释放 COM 指针：
    /// 返回 false 表示线程仍存活（极可能卡在 <c>IMFSourceReader</c> 的原生调用内），此时释放即为 use-after-free（<c>0x80131506</c>）。
    /// 释放的唯一安全判据是 gate 排空成功，本返回值仅作辅助诊断。</para>
    /// <para>异步释放链请改用 <see cref="ShutdownAsync"/>，本方法会阻塞调用线程最长 <paramref name="timeout"/>。</para>
    /// </remarks>
    public bool Shutdown(TimeSpan timeout)
    {
        BeginShutdown();

        if (_thread.IsAlive && timeout > TimeSpan.Zero)
            _thread.Join(timeout);

        bool exited = !_thread.IsAlive;
        if (exited)
            DisposeQueue();
        return exited;
    }

    /// <summary>
    /// 优雅关闭（异步）：语义同 <see cref="Shutdown(TimeSpan)"/>，但<b>不阻塞调用线程</b>。
    /// </summary>
    /// <param name="timeout">等待线程退出信号的最长时间。</param>
    /// <returns>线程循环是否已退出（true=已退出；false=超时）。</returns>
    /// <remarks>
    /// <para>等待的是 <c>Loop</c> 的 <c>finally</c> 中置位的退出信号（此时 <c>foreach</c> 已结束、COM 已反初始化），
    /// 而非 <see cref="Thread.Join(TimeSpan)"/>——后者会把最长数秒的同步阻塞引入 <c>DisposeAsync</c> 链路，
    /// 违反「真异步方法一路 await 到底、禁止硬同步阻塞」的准则。</para>
    /// <para>与 <see cref="Shutdown(TimeSpan)"/> 并发调用是安全的：<c>CompleteAdding</c> 与队列释放各自 Interlocked 幂等。</para>
    /// </remarks>
    public ValueTask<bool> ShutdownAsync(TimeSpan timeout)
    {
        BeginShutdown();

        // 快路径：线程已退出 —— 不分配状态机。
        if (_exited.Task.IsCompletedSuccessfully)
        {
            DisposeQueue();
            return new ValueTask<bool>(true);
        }

        // 非正超时按「立即判定未退出」处理，避免 WaitAsync 对负超时抛 ArgumentOutOfRangeException。
        if (timeout <= TimeSpan.Zero)
            return new ValueTask<bool>(false);

        return AwaitExitCore(timeout);
    }

    private async ValueTask<bool> AwaitExitCore(TimeSpan timeout)
    {
        try
        {
            await _exited.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 线程仍卡在原生调用内：不释放队列（线程仍可能触碰它），交由调用方按 gate 结果决定泄漏与否。
            return false;
        }

        DisposeQueue();
        return true;
    }

    /// <inheritdoc cref="Shutdown(TimeSpan)"/>
    public void Dispose() => Shutdown(MediaPipelineTimeouts.SchedulerJoin);
}
