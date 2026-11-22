using System;
using Vector3 = System.Numerics.Vector3;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

// SkiaGpuVideoRenderer 的 Diagnostics 职责段（部分类，由 SkiaGpuVideoRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/Render/Dispose）在主文件；GPU 共享表面合成呈现与诊断。
internal sealed partial class SkiaGpuVideoRenderer
{
    /// <summary>渲染线程完成一帧采样绘制后回调（记录心跳，供写入侧判定渲染线程是否停滞）。</summary>
    internal void OnDrawn()
    {
        Interlocked.Increment(ref _drawSeq);
        Volatile.Write(ref _lastDrawQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _framesSinceLastDraw, 0);
    }

    /// <summary>渲染线程绘制诊断（DrawOp 调用：[DRAW-OP] 心跳/wrap 遥测/几何对账，Trace 级）。</summary>
    internal void LogDrawGeometry(string message)
        => _logger.LogTrace("[SKIA-GPU-DRAW] {Message}", message);

    /// <summary>合成 Custom Visual 的诊断通道（渲染线程 handler → ILogger）。</summary>
    internal void LogCompositionDiagnostic(string message)
        => _logger.LogInformation("[SKIA-GPU] {Message}", message);

    /// <summary>渲染线程回调：绘制失败计数（达阈值请求源级切换；全部源穷尽后触发 Unhealthy）。</summary>
    internal void OnDrawFailure(string reason)
    {
        // 失败原因按内容去重（不同原因各打一次）——全局一次性会吞掉换源后新形态的失败细节。
        if (reason != _lastFailureReason)
        {
            _lastFailureReason = reason;
            _logger.LogWarning("[SKIA-GPU] 渲染线程直绘失败：{Reason}（连续失败将切换共享表面源）。", reason);
        }
        RegisterFailure();

        // 保底驱动：失败帧没有任何像素变化，若宿主合成循环因此不再调度渲染回调，
        // 失败计数与源级切换都无从推进——主动请求一次重绘以维持失败处理循环。
        var control = _control;
        if (control is not null)
            Dispatcher.UIThread.Post(() => control.InvalidateVisual());
    }

    private void RegisterFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (_unhealthyFired != 0)
            return;
        if (failures < SourceFailureThreshold)
            return;

        // 源级回退：还有未尝试的源 → 请求切换（Present 在管线线程执行）；穷尽 → Unhealthy。
        if (_triedCount < _availableFactories.Count)
        {
            if (Interlocked.Exchange(ref _sourceSwitchPending, 1) == 0)
            {
                _logger.LogWarning(
                    "[SKIA-GPU] 连续 {N} 帧呈现失败，请求切换共享表面源（{Done}/{Total}）。",
                    failures, _triedCount, _availableFactories.Count);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            return;
        }

        if (Interlocked.CompareExchange(ref _unhealthyFired, 1, 0) == 0)
        {
            _logger.LogWarning(
                "[SKIA-GPU] 全部 {Total} 个共享表面源呈现失败，触发渲染器回退（软渲染）。",
                _availableFactories.Count);
            Unhealthy?.Invoke();
        }
    }
}
