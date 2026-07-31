using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

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
/// <para><b>生命周期</b>：<see cref="Dispose"/> 调用 <see cref="BlockingCollection{T}.CompleteAdding"/> 后等待线程排空队列退出
/// （最多 2s）。调用方须保证 <see cref="Dispose"/> 时不再有在途任务（本类仅由 <c>MFDemuxer</c> 在
/// 读取线程已退出、无在途 <c>ReadPacketAsync</c>/<c>SeekAsync</c> 时调用）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，纯 BCL 类型，无反射。</para>
/// </remarks>
internal sealed class SingleThreadTaskScheduler : TaskScheduler, IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<Task> _tasks = new(new ConcurrentQueue<Task>());
    private bool _disposed;

    public SingleThreadTaskScheduler(string name)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
    }

    private void Loop()
    {
        // GetConsumingEnumerable 在 CompleteAdding 且队列排空后自动结束，线程随之退出。
        foreach (var task in _tasks.GetConsumingEnumerable())
            TryExecuteTask(task);
    }

    protected override void QueueTask(Task task) => _tasks.Add(task);

    // 永不在调用方线程内联执行——所有任务必须在专用线程上跑，保证 COM 对象单线程亲和。
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 完成入队、允许线程排空所有待执行任务后退出（不取消在途任务，避免调用方 await 悬挂）。
        try { _tasks.CompleteAdding(); } catch (InvalidOperationException) { /* 已 CompleteAdding */ }

        // 最多等待 2s 让线程排空后退出；超时仅发生于异常挂死，放弃等待（后台线程无害）。
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
    }
}
