using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// <see cref="NativeCallGate"/> 纯托管单元测试（不依赖 MediaFoundation / COM）。
/// 验证两阶段关闭协议的核心不变量：I1 单调性、I2 稳定性、I3 独占性、I4 安全侧默认、I5 无死锁、I6 配对性。
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class NativeCallGateTests
{
    [Fact]
    public void TryEnter_BeforeClose_ReturnsTrue()
    {
        var gate = new NativeCallGate();

        bool entered = gate.TryEnter();

        entered.Should().BeTrue();
        gate.Exit();
    }

    [Fact]
    public void TryEnter_AfterBeginClose_ReturnsFalse()
    {
        var gate = new NativeCallGate();
        gate.BeginClose();

        gate.TryEnter().Should().BeFalse();
    }

    [Fact]
    public void WaitDrain_WhenNoInFlightAndClosed_ReturnsTrueImmediately()
    {
        var gate = new NativeCallGate();
        gate.BeginClose();

        gate.WaitDrain(TimeSpan.FromMilliseconds(100)).Should().BeTrue();
    }

    [Fact]
    public void WaitDrain_WithInFlightCall_TimesOutThenDrainsAfterExit()
    {
        var gate = new NativeCallGate();
        gate.TryEnter();        // 模拟一个在途原生调用，且不退出
        gate.BeginClose();

        // 在途调用未退出 ⇒ 超时返回 false（I4：安全侧默认，不释放）
        gate.WaitDrain(TimeSpan.FromMilliseconds(50)).Should().BeFalse();

        // 在途调用退出 ⇒ 排空，后续 WaitDrain 立即返回 true（I2 稳定性）
        gate.Exit();
        gate.WaitDrain(TimeSpan.FromMilliseconds(50)).Should().BeTrue();
    }

    [Fact]
    public async Task WaitDrainAsync_CompletesWhenInFlightExits()
    {
        var gate = new NativeCallGate();
        gate.TryEnter();
        gate.BeginClose();

        // 在途未退出 ⇒ 超时 false（I4）。用短超时，避免把等待时长白白计入测试耗时。
        var drained = await gate.WaitDrainAsync(TimeSpan.FromMilliseconds(50));
        drained.Should().BeFalse("在途调用尚未退出，drain 不应成功");

        gate.Exit();
        (await gate.WaitDrainAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
    }

    [Fact]
    public async Task WaitDrainAsync_WhenAlreadyDrained_ReturnsTrueSynchronously()
    {
        // 审计 A-5：异步版必须与同步版逐条对齐——已排空时立即返回 true，且走同步完成路径（不分配状态机）。
        var gate = new NativeCallGate();
        gate.BeginClose();

        var pending = gate.WaitDrainAsync(TimeSpan.FromSeconds(5));

        pending.IsCompleted.Should().BeTrue("已排空应走同步快路径，不应产生真实的异步等待");
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task WaitDrainAsync_WithNonPositiveTimeout_ReturnsFalseInsteadOfThrowing()
    {
        // 审计 A-5：同步版 WaitDrain 对 timeout<=0 返回 false；异步版若直接透传给 Task.WaitAsync
        // 会对负值抛 ArgumentOutOfRangeException ⇒ 关闭路径上冒出意外异常。必须对齐为 false。
        var gate = new NativeCallGate();
        gate.TryEnter();
        gate.BeginClose();

        (await gate.WaitDrainAsync(TimeSpan.Zero)).Should().BeFalse();
        (await gate.WaitDrainAsync(TimeSpan.FromMilliseconds(-5))).Should().BeFalse();
    }

    [Fact]
    public void BeginClose_IsIdempotent()
    {
        var gate = new NativeCallGate();

        gate.BeginClose();
        gate.BeginClose(); // 第二次不应抛异常或改变结论

        gate.WaitDrain(TimeSpan.FromMilliseconds(50)).Should().BeTrue();
    }

    [Fact]
    public void Exit_WithoutEnter_DoesNotThrowOrUnderflow()
    {
        var gate = new NativeCallGate();

        var act = () => gate.Exit();

        act.Should().NotThrow();
        gate.BeginClose();
        act.Should().NotThrow();
    }

    [Fact]
    public void ConcurrentEnterExit_AllDrain_AfterBeginClose()
    {
        var gate = new NativeCallGate();
        const int threadCount = 16;
        using var startSignal = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                startSignal.Wait();
                // 每个线程多次进入/退出，模拟高频原生调用
                for (int k = 0; k < 50; k++)
                {
                    if (gate.TryEnter())
                    {
                        try { Thread.SpinWait(10); }
                        finally { gate.Exit(); }
                    }
                    else
                    {
                        // 已关闸，直接退出（真实调用方语义）
                        break;
                    }
                }
            });
            threads[i].Start();
        }

        // 让一部分调用先进入，再关闸
        Thread.Sleep(20);
        startSignal.Set();
        Thread.Sleep(20);
        gate.BeginClose();

        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(5));

        // 关闸后所有在途调用已退出 ⇒ 排空必然成功（I3 独占性可由关闭线程安全释放）
        gate.WaitDrain(TimeSpan.FromSeconds(2)).Should().BeTrue(
            "关闸后所有进入的调用都通过 try/finally Exit 退出，drain 必须成功");
    }

    [Fact]
    public void UnpairedExit_StillAllowsDrain_NoDeadlock()
    {
        // I6：未配对退化为泄漏（仍不崩）。这里验证 Exit 漏配不会导致关闭线程永久挂死。
        var gate = new NativeCallGate();
        gate.TryEnter();   // 进入一次
        // 故意不 Exit（模拟调用方 bug）
        gate.BeginClose();

        // 漏配导致 drain 超时（安全失败模式），但不死锁
        gate.WaitDrain(TimeSpan.FromMilliseconds(50)).Should().BeFalse();
    }
}
