using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LingFan.Media.Backends.MediaFoundation.Concurrency;

/// <summary>
/// 原生调用闸：把"是否在途持有 COM 指针"建模为一个可在关闭时单调收敛的不变量，
/// 使释放安全性不再依赖调用方守约，也不再依赖超时赌博。
/// </summary>
/// <remarks>
/// <para>本类是 MF 冷启动 <c>COR_E_EXECUTIONENGINE</c>（原生堆损坏）修复的核心构件。
/// 旧逻辑在关闭时「超时后继续 <c>Marshal.Release</c>」，跨线程对在途原生调用形成 use-after-free。
/// 本类用 <see cref="Monitor"/> 对在途原生调用计数，关闭时先 <see cref="BeginClose"/> 截断新进入，
/// 再等待在途调用排空；排空成功才释放，失败则有意泄漏（不释放、不置 NULL、不清容器）。</para>
/// <para>不变量（由调用方严格配对 <see cref="TryEnter"/>/<see cref="Exit"/> 维护）：</para>
/// <list type="bullet">
/// <item>单调性：<see cref="BeginClose"/> 后 <see cref="TryEnter"/> 恒返回 false ⇒ 在途计数单调不增。</item>
/// <item>稳定性：由单调性，<c>_inFlight==0 &amp;&amp; _closing</c> 一旦成立便永久成立，故 WaitDrain 返回 true 是稳定结论（无 ABA）。</item>
/// <item>独占性：由稳定性，WaitDrain==true 之后不存在任何线程处于闸内 ⇒ 闸内引用资源可由关闭线程独占释放。</item>
/// <item>安全侧默认：WaitDrain==false 时不满足独占性 ⇒ 不释放（只泄漏不崩）。</item>
/// <item>无死锁：<see cref="Exit"/> 在 lock 内仅 PulseAll + TrySetResult；TCS 用 RunContinuationsAsynchronously，续体不在 lock 内执行。</item>
/// <item>配对性：TryEnter/Exit 必须 try/finally 严格配对。<b>两种失配的后果并不对称</b>——
/// <b>漏 Exit</b>：计数永不归零 ⇒ drain 超时 ⇒ 有意泄漏，**安全侧**失败；
/// <b>多 Exit</b>：把他人的计数减掉 ⇒ 可能<b>提前</b>判定排空 ⇒ 释放仍在使用的指针 ⇒ use-after-free，**危险侧**失败。
/// 故该独占性的成立以「不存在多余 Exit」为前提，该前提由调用点穷举核查保证（详见 <see cref="Exit"/> 备注）。</item>
/// </list>
/// <para><b>仅用 Monitor</b>（不用 ManualResetEventSlim/SemaphoreSlim），刻意规避"等待原语自身何时释放"的二阶生命周期问题；本类不实现 <see cref="IDisposable"/>。</para>
/// <para><b>AOT 兼容</b>：纯 BCL，无反射、零 <c>[ComImport]</c>。</para>
/// </remarks>
internal sealed class NativeCallGate
{
    private readonly object _gate = new();
    private int _inFlight;                 // 在途原生调用数（仅 lock 内读写）
    private bool _closing;                 // 关闸标志（仅 lock 内写）
    private volatile bool _closingFast;    // 无锁快路径镜像，仅用于 IsClosing 读
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously); // 必须异步续体，防在 lock 内跑续体

    /// <summary>是否已关闸（无锁快路径，仅供调用方在闸内快速决定退出，避免空转）。</summary>
    public bool IsClosing => _closingFast;

    /// <summary>
    /// 进入原生调用区。返回 false 表示已关闸，调用方必须立即返回、绝不触碰原生指针。
    /// 须与 <see cref="Exit"/> 严格 try/finally 配对。
    /// </summary>
    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_closing) return false;
            _inFlight++;
            return true;
        }
    }

    /// <summary>
    /// 离开原生调用区。必须与 <see cref="TryEnter"/>==true 严格 try/finally 配对。
    /// </summary>
    /// <remarks>
    /// <para>多余 Exit（未 Enter 即 Exit）被钳制为 no-op 以避免计数下溢，但钳制<b>不能</b>消除其危害：
    /// 若多余 Exit 发生在他人在途期间，会把计数错误地减到 0，令 drain 提前成功 ⇒ 提前释放 ⇒ UAF。
    /// 因此该独占性以「不存在多余 Exit」为前提。</para>
    /// <para>该前提由<b>穷举核查</b>而非运行时检测保证：本类为 <c>internal</c>，Enter/Exit 调用点有限且全部为
    /// <c>if (TryEnter()) { try { … } finally { Exit(); } }</c> 定式
    /// （MFDemuxer 3 处：OpenCore / ReadPacketCore / SeekAsync 的 lambda；
    /// MFVideoDecoder 4 处：DecodeAsync / RenegotiateOutput / FlushAsync / Reset。调用点逐处核验）。</para>
    /// <para><b>允许嵌套</b>：本闸是计数器而非互斥锁，<c>MFVideoDecoder.RenegotiateOutput</c> 即在
    /// DecodeAsync/FlushAsync 的闸内再次 Enter（计数 1→2→1），配对性不受影响。
    /// 唯一的行为差异是：若在外层持闸期间发生 <see cref="BeginClose"/>，内层 <see cref="TryEnter"/> 会返回 false，
    /// 此时 <c>RenegotiateOutput</c> 返回 false ⇒ 解码优雅失败。该退化<b>正是关闭期应有的语义</b>，
    /// 且因内层未进入也就不会 Exit，绝不会产生多余 Exit。</para>
    /// <para>刻意<b>不</b>用 <c>Debug.Assert</c> 做下溢检测：.NET 下断言失败默认走 <c>Environment.FailFast</c>，
    /// 会把「本可只泄漏」升级为进程自杀，与本修复的初衷（绝不让基础设施库杀进程）直接冲突。</para>
    /// </remarks>
    public void Exit()
    {
        lock (_gate)
        {
            if (_inFlight > 0) _inFlight--;
            if (_inFlight == 0 && _closing)
            {
                Monitor.PulseAll(_gate);
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>关闸（幂等）。此后 <see cref="TryEnter"/> 恒 false。</summary>
    public void BeginClose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            _closingFast = true;
            if (_inFlight == 0)
            {
                Monitor.PulseAll(_gate);
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>同步等待排空；前置：已 <see cref="BeginClose"/>。返回 false=超时（未排空，应有意泄漏）。</summary>
    /// <remarks>
    /// 剩余时间以 <see cref="Stopwatch"/> 实测推进（而非按名义 waitMs 递减），
    /// 从而对「被唤醒但条件未满足」的情形也保持总等待时长精确不超时早退。
    /// </remarks>
    public bool WaitDrain(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_inFlight == 0 && _closing) return true;

            long totalMs = (long)timeout.TotalMilliseconds;
            if (totalMs <= 0) return false;

            long startTs = Stopwatch.GetTimestamp();
            while (true)
            {
                long elapsedMs = (long)Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
                long remainingMs = totalMs - elapsedMs;
                if (remainingMs <= 0) return false;

                Monitor.Wait(_gate, (int)Math.Min(remainingMs, int.MaxValue));
                if (_inFlight == 0 && _closing) return true;
            }
        }
    }

    /// <summary>异步等待排空；前置：已 <see cref="BeginClose"/>。返回 false=超时。绝不阻塞调用线程。</summary>
    /// <remarks>
    /// 语义与 <see cref="WaitDrain"/> 逐条对齐：已排空即刻 true（不分配状态机）；
    /// 非正超时即刻 false（而非让 <c>Task.WaitAsync</c> 对负值抛 <see cref="ArgumentOutOfRangeException"/>）。
    /// <c>_drained</c> 仅在 <c>_closing==true</c> 时被置位，故"完成"⇔ 同步版的 <c>_inFlight==0 &amp;&amp; _closing</c>。
    /// </remarks>
    public ValueTask<bool> WaitDrainAsync(TimeSpan timeout)
    {
        if (_drained.Task.IsCompletedSuccessfully) return new ValueTask<bool>(true);
        if (timeout <= TimeSpan.Zero) return new ValueTask<bool>(false);
        return AwaitDrainCore(timeout);
    }

    private async ValueTask<bool> AwaitDrainCore(TimeSpan timeout)
    {
        try
        {
            await _drained.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
