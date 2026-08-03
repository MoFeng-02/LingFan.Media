using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// <see cref="SingleThreadTaskScheduler"/> 纯托管单元测试（不触碰 MF/COM 对象，仅验证线程亲和与关闭协议）。
/// </summary>
/// <remarks>
/// 覆盖审计 A-3（异步关闭不得用 <c>Thread.Join</c> 阻塞调用线程）与 A-6（关闭幂等、队列只释放一次）。
/// </remarks>
[Trait("Category", "Concurrency")]
public sealed class SingleThreadTaskSchedulerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AllTasks_RunOnSameDedicatedThread()
    {
        // 该调度器存在的唯一理由：把 IMFSourceReader 的全部 COM 调用钉在同一条线程上。
        // 一旦这条性质被破坏，0x80131506 就会以"若干次成功读取后随机崩溃"的形式回归。
        using var scheduler = new SingleThreadTaskScheduler("Test-Affinity");
        var factory = new TaskFactory(scheduler);

        var ids = new int[32];
        var tasks = new Task[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            int k = i;
            tasks[k] = factory.StartNew(() => ids[k] = Environment.CurrentManagedThreadId);
        }
        await Task.WhenAll(tasks);

        ids.Distinct().Should().HaveCount(1, "所有任务必须在同一条专用线程上执行");
        ids[0].Should().NotBe(Environment.CurrentManagedThreadId, "绝不允许在调用方线程内联执行");
    }

    [Fact]
    public async Task ShutdownAsync_DrainsQueuedTasks_AndReturnsTrue()
    {
        var scheduler = new SingleThreadTaskScheduler("Test-ShutdownAsync");
        var factory = new TaskFactory(scheduler);

        int executed = 0;
        var queued = new Task[8];
        for (int i = 0; i < queued.Length; i++)
            queued[i] = factory.StartNew(() => Interlocked.Increment(ref executed));

        bool exited = await scheduler.ShutdownAsync(WaitLimit);

        exited.Should().BeTrue("无在途阻塞任务时线程必须在超时内退出");
        await Task.WhenAll(queued);
        executed.Should().Be(queued.Length, "CompleteAdding 不取消已入队任务，须排空后再退出");
    }

    [Fact]
    public async Task ShutdownAsync_DoesNotBlockCallingThread()
    {
        // A-3 回归防线：异步关闭必须等待「线程退出信号」，而不是 Thread.Join。
        // 用一个长时间占住调度器线程的任务模拟"卡在原生调用内"，异步等待应在超时后返回 false，
        // 且期间调用线程可继续推进其他 await（这里以并发计数体现）。
        var scheduler = new SingleThreadTaskScheduler("Test-NoBlock");
        var factory = new TaskFactory(scheduler);

        using var release = new ManualResetEventSlim(false);
        var blocking = factory.StartNew(() => release.Wait(WaitLimit));

        int progressed = 0;
        var shutdownTask = scheduler.ShutdownAsync(TimeSpan.FromMilliseconds(150)).AsTask();
        var progressTask = Task.Run(async () =>
        {
            while (!shutdownTask.IsCompleted)
            {
                Interlocked.Increment(ref progressed);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        (await shutdownTask).Should().BeFalse("线程仍被占用，超时应返回 false 而非无限等待");
        progressed.Should().BeGreaterThan(0, "等待期间调用方线程未被阻塞");

        release.Set();
        await blocking;
        await progressTask;
        (await scheduler.ShutdownAsync(WaitLimit)).Should().BeTrue("任务释放后线程应退出");
    }

    [Fact]
    public async Task Shutdown_IsIdempotent_AcrossSyncAndAsyncPaths()
    {
        // A-6：Shutdown / ShutdownAsync / Dispose 可任意组合重复调用，
        // CompleteAdding 与队列 Dispose 各自只执行一次，且不得抛异常。
        var scheduler = new SingleThreadTaskScheduler("Test-Idempotent");

        scheduler.Shutdown(WaitLimit).Should().BeTrue();
        (await scheduler.ShutdownAsync(WaitLimit)).Should().BeTrue();
        scheduler.Shutdown(WaitLimit).Should().BeTrue();

        var act = () => scheduler.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void QueueAfterShutdown_SurfacesAsException_NotSilentHang()
    {
        // 关闭后入队必须"快速失败"，绝不能让任务既不运行也不完成 —— 那会让上游 await 永久挂死。
        // 抛出的 TaskSchedulerException / InvalidOperationException（ObjectDisposedException 亦派生自后者）
        // 由 MFDemuxer 在关闸期捕获并按 EOS 收尾（审计 A-4：仅在关闸期捕获）。
        var scheduler = new SingleThreadTaskScheduler("Test-QueueAfterShutdown");
        var factory = new TaskFactory(scheduler);
        scheduler.Shutdown(WaitLimit).Should().BeTrue();

        // 用块体 lambda 绑定 Record.Exception(Action) 重载：Func<object> 重载在返回值为 Task 时会被 xunit 拒绝。
        var ex = Record.Exception(() => { _ = factory.StartNew(static () => { }, TestContext.Current.CancellationToken); });

        ex.Should().NotBeNull("关闭后入队应立即以异常暴露，而非静默挂起");
        (ex is TaskSchedulerException or InvalidOperationException).Should().BeTrue(
            "实际异常类型为 {0}", ex!.GetType().Name);
    }
}
