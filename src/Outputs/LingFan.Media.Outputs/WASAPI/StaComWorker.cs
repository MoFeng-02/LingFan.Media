using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// 专用 STA COM 工作线程（单一职责组件）：提供"一个线程、一个 COM 单元、所有调用同线程"的执行上下文。
/// </summary>
/// <remarks>
/// <para><b>唯一职责</b>：线程与 COM 单元的生命周期管理 + 工作项投递。本类<b>不认识任何 WASAPI 概念</b>，
/// 不持有任何 COM 指针——持有者是使用它的组件（如 <see cref="WasapiAudioEngine"/>）。</para>
/// <para><b>COM 单元亲和</b>：在专用线程上创建的 COM 对象，其 <c>Release</c> 必须
/// ①在同一线程执行、②先于该线程的 <c>CoUninitialize</c>。本类通过
/// <see cref="Shutdown"/> 的 <c>releaseOnWorker</c> 回调保证这一顺序：回调在工作线程内执行完毕后，
/// 线程 proc 的 <c>finally</c> 才调用 <c>CoUninitialize</c>。</para>
/// <para><b>与 <see cref="WasapiRenderLoop"/> 的关系</b>：RenderLoop 有自己内建的 STA 线程（还兼做音频帧写入、
/// 关闭信号、背压等），本类<b>不改动它</b>——那条路径承载着已验收的 frame pacing 修复，重构风险大于收益。
/// 本类是给"纯控制类"COM 组件用的最小实现；后续若要统一线程模型，可把 RenderLoop 迁移到本类之上。</para>
/// <para><b>AOT</b>：无反射、无 <c>[ComImport]</c>，仅 <c>Thread</c> + 队列 + 事件。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class StaComWorker : IDisposable
{
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private volatile bool _shutdownRequested;
    private bool _disposed;

    /// <summary>
    /// 创建并启动 STA 工作线程（构造返回时线程已完成 <c>CoInitializeEx</c>，可立即投递工作）。
    /// </summary>
    /// <param name="threadName">线程名（便于调试器/性能分析器识别）。</param>
    public StaComWorker(string threadName)
    {
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = threadName };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>当前线程是否就是本工作线程（用于避免自投递死锁）。</summary>
    public bool IsOnWorkerThread => ReferenceEquals(Thread.CurrentThread, _thread);

    /// <summary>
    /// 在工作线程执行动作并<b>阻塞等待</b>其完成，异常按原栈透传给调用方。
    /// </summary>
    /// <param name="action">要在 STA 线程内执行的动作。</param>
    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 自投递保护：已在工作线程上时直接执行，否则 Done.Wait() 会永久死锁（本线程正是消费者）。
        if (IsOnWorkerThread)
        {
            action();
            return;
        }

        var item = new WorkItem(action);
        Post(item);
        item.Done.Wait();
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
    }

    /// <summary>
    /// 在工作线程执行动作并<b>异步等待</b>其完成（调用线程不被占用），异常经 Task 传播。
    /// </summary>
    /// <param name="action">要在 STA 线程内执行的动作。</param>
    /// <param name="ct">取消令牌。仅中止<b>等待</b>；已投递的动作仍会在工作线程跑完（原生初始化不可回滚）。</param>
    /// <remarks>
    /// 这不是伪异步：动作<b>必须</b>在特定 STA 线程执行（COM 单元亲和是硬约束），
    /// 调用方 <c>await</c> 的是真实的跨线程完成信号，而非把同步工作丢进线程池假装并发。
    /// </remarks>
    public Task RunAsync(Action action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsOnWorkerThread)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(action) { Completion = tcs };
        Post(item);
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>
    /// 关闭工作线程：先在工作线程内执行 <paramref name="releaseOnWorker"/>（释放 COM 对象），
    /// 再由线程 proc 的 finally 执行 <c>CoUninitialize</c>，最后 Join。
    /// </summary>
    /// <param name="releaseOnWorker">在工作线程内执行的释放动作（可空）。异常被吞并忽略——关闭路径不抛。</param>
    public void Shutdown(Action? releaseOnWorker)
    {
        if (_shutdownRequested) return;
        _shutdownRequested = true;

        try
        {
            var item = new WorkItem(releaseOnWorker) { IsShutdown = true };
            Post(item);
            item.Done.Wait();
        }
        catch
        {
            // 关闭路径不抛：即使投递失败也要继续 Join，避免调用方卡住。
        }

        try { _thread.Join(); } catch { /* 关闭期忽略 */ }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown(null);
        _signal.Dispose();
        _ready.Dispose();
    }

    private void Post(WorkItem item)
    {
        _queue.Enqueue(item);
        _signal.Set();
    }

    private void ThreadProc()
    {
        WasapiInterop.CoInitializeEx(IntPtr.Zero, WasapiInterop.COINIT_APARTMENTTHREADED);
        _ready.Set();
        try
        {
            while (true)
            {
                while (_queue.TryDequeue(out var item))
                {
                    if (item.IsShutdown)
                    {
                        // COM 释放必须同线程且先于 CoUninitialize（由本 return → finally 保证顺序）。
                        try { item.Action?.Invoke(); } catch { /* 关闭期忽略 */ }
                        item.Done.Set();
                        item.Completion?.TrySetResult();
                        return;
                    }

                    try
                    {
                        item.Action?.Invoke();
                        item.Completion?.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        item.Exception = ex;
                        item.Completion?.TrySetException(ex);
                    }
                    finally
                    {
                        item.Done.Set();
                    }
                }

                // AutoResetEvent 保证不丢唤醒：若在排空后、WaitOne 前发生 Post，等待会立即返回。
                _signal.WaitOne();
            }
        }
        finally
        {
            WasapiInterop.CoUninitialize();
        }
    }

    /// <summary>工作项：待在 STA 线程执行的动作 + 完成信号（同步事件与可选的异步 TCS 双通道）。</summary>
    private sealed class WorkItem(Action? action)
    {
        public readonly Action? Action = action;
        public readonly ManualResetEventSlim Done = new(false);
        public TaskCompletionSource? Completion;
        public Exception? Exception;
        public bool IsShutdown;
    }
}
