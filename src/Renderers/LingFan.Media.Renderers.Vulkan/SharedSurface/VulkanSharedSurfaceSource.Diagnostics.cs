using LingFan.Media.GPUShare.Vulkan;
using LingFan.Media.Renderers.Shared;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的 Diagnostics 职责段（部分类，由 VulkanSharedSurfaceSource.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/契约属性/Dispose）在主文件；平台专属路径见 Android/Apple partial。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
    /// <summary>记录当前写入阶段（Volatile 写，供看门狗定时器线程读取）。</summary>
    private void MarkStage(string stage)
    {
        Volatile.Write(ref _lastStage, stage);
        Volatile.Write(ref _lastStageQpc, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>懒启动停摆看门狗定时器（首次写入时创建，Dispose 时停止）。</summary>
    private void EnsureStallTimer()
    {
        if (_stallTimer is not null)
            return;
        var timer = new System.Threading.Timer(StallTick, null, 2000, 2000);
        if (Interlocked.CompareExchange(ref _stallTimer, timer, null) is not null)
            timer.Dispose();
    }

    private void StallTick(object? state)
    {
        if (_disposed || Volatile.Read(ref _stageActive) == 0)
            return;
        long stageStart = Volatile.Read(ref _lastStageQpc);
        double stuckSec = System.Diagnostics.Stopwatch.GetElapsedTime(stageStart).TotalSeconds;
        if (stuckSec < 3.0)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (stageStart == Volatile.Read(ref _stallMarkQpc) && now < Volatile.Read(ref _stallNextReportQpc))
            return;
        Volatile.Write(ref _stallMarkQpc, stageStart);
        Volatile.Write(ref _stallNextReportQpc, now + 10 * System.Diagnostics.Stopwatch.Frequency);
        _logger.LogError(
            "[VSS-STALL] 共享表面写入疑似停摆：帧={Seq} 卡在={Stage} 已停留={Sec:F1}s（未返回=阻塞在该阶段内部的原生调用）",
            Volatile.Read(ref _writeSeq), Volatile.Read(ref _lastStage) ?? "<unknown>", stuckSec);
    }
}
