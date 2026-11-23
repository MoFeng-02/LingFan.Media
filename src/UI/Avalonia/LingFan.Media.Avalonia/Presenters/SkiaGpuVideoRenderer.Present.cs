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

// SkiaGpuVideoRenderer 的帧交付与源切换职责段（部分类，按职责拆分，编译期等价）。
// Present（管线线程写入共享表面 + 停滞看门狗）与源级回退切换；Attach/Render/Dispose 在主文件。
internal sealed partial class SkiaGpuVideoRenderer
{
    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        if (_disposed)
            return;

        // 源级回退：渲染线程失败达阈值后置位请求，由本（管线）线程串行执行切换；
        // 切换与旧源释放在渲染线程当帧结束后进行，无并发采样。
        if (Interlocked.Exchange(ref _sourceSwitchPending, 0) == 1)
            SwitchSourceCore();

        if (_source is null)
        {
            RegisterFailure();
            return;
        }

        try
        {
            if (!_source.TryWriteFrame(frame, out SharedGpuSurfaceDescriptor desc) || !desc.IsValid)
            {
                RegisterFailure();
                return;
            }

            lock (_gate)
            {
                _pending = desc;
                _hasPending = true;
            }

            // 有新待呈现帧：预约一次合成时钟重绘（vsync 对齐、去重合并）。
            RequestRenderTick();

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                _logger.LogInformation(
                    "[SKIA-GPU] 首帧已写入共享表面 {W}x{H} version={Version}，等待渲染线程直绘。",
                    desc.Width, desc.Height, desc.Version);
            }
            _consecutiveFailures = 0;

            // 交付到渲染线程的入口心跳（不依赖合成循环）：证明帧确实抵达渲染器输入端。
            // 若本条出现而下游无绘制 ⇒ 卡在 Avalonia 渲染循环/合成调度，而非解码或导入链。
            // Trace 级（每 300 帧）：呈现线程为实时线程，Information 写 logcat（双 provider）
            // 单次开销显著，不应周期性落在呈现线程；[SKIA-GPU-STALL] 与 [SYNC] 承担异常可见性。
            _deliveredFrames++;
            if ((_deliveredFrames % 300) == 1)
                _logger.LogTrace(
                    "[SKIA-GPU] 已交付渲染线程 #{N} v={Version} kind={Kind} {W}x{H}",
                    _deliveredFrames, desc.Version, desc.Kind, desc.Width, desc.Height);

            // 渲染线程消费心跳检查：管线持续写入而渲染线程长时间未绘制 ⇒ 画面可能定格。
            // 仅告警不干预——真实修复取决于渲染线程侧状态（包装失败已有 OnDrawFailure 通道）。
            var lastDraw = Volatile.Read(ref _lastDrawQpc);
            // 从未成功绘制（lastDraw==0）也算停滞，避免掩盖「渲染线程从未上屏」。
            if (Interlocked.Increment(ref _framesSinceLastDraw) > 90)
            {
                double drawIdleSec = lastDraw == 0
                    ? double.PositiveInfinity
                    : System.Diagnostics.Stopwatch.GetElapsedTime(lastDraw).TotalSeconds;
                if (drawIdleSec > 3.0)
                {
                    if (lastDraw == 0)
                    {
                        _logger.LogWarning(
                            "[SKIA-GPU-STALL] 管线已连续写入 {N} 帧，渲染线程【从未成功绘制】⇒ 上屏路径未工作（合成循环未驱动或全部包装失败）。",
                            _framesSinceLastDraw);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[SKIA-GPU-STALL] 管线已连续写入 {N} 帧，渲染线程 {Sec:F1}s 未绘制（最后绘制序号={Seq}）⇒ 画面可能定格，管线侧计数与真实上屏脱节。",
                            _framesSinceLastDraw, drawIdleSec, Volatile.Read(ref _drawSeq));
                    }
                    Interlocked.Exchange(ref _framesSinceLastDraw, 0);

                    // 合成帧循环看门狗：已挂 CompositionCustomVisual 却从未收到帧回调
                    // （render loop 未驱动）⇒ 卸载并回退 DrawOp 路径，避免新上屏路径拖死既有路线。
                    if (_compositionVisual is not null && (_compositionHandler?.FrameLoopCount ?? 0) == 0)
                    {
                        _logger.LogWarning(
                            "[SKIA-GPU] 合成帧循环零回调（render loop 未驱动），卸载 CompositionCustomVisual 回退 DrawOp 路径。");
                        DetachCompositionCustomVisual();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkiaGpuVideoRenderer Present 失败。");
            RegisterFailure();
        }
    }

    /// <summary>
    /// 切换到下一个共享表面源（管线线程串行调用）：释放当前源并创建下一候选；
    /// 全部候选穷尽时触发 Unhealthy 交由宿主整体回退。
    /// </summary>
    private void SwitchSourceCore()
    {
        while (_triedCount < _availableFactories.Count)
        {
            var factory = _availableFactories[_triedCount];
            _triedCount++;
            try
            {
                var candidate = factory.Create();
                _source?.Dispose();
                _source = candidate;
                _firstFrameLogged = false;
                lock (_gate)
                {
                    _hasPending = false;
                    _pending = default;
                }
                _logger.LogInformation(
                    "[SKIA-GPU] 已切换共享表面源={Source}（{Done}/{Total}）。",
                    _source.GetType().Name, _triedCount, _availableFactories.Count);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "共享表面源工厂（{Factory}）Create 失败，尝试下一个。",
                    factory.GetType().Name);
            }
        }

        // 全部候选穷尽：交由宿主整体回退（CPU Skia 兜底）。
        if (Interlocked.CompareExchange(ref _unhealthyFired, 1, 0) == 0)
        {
            _logger.LogWarning(
                "[SKIA-GPU] 全部 {Total} 个共享表面源尝试完毕，触发渲染器回退（软渲染）。",
                _availableFactories.Count);
            Unhealthy?.Invoke();
        }
    }

    private void UnsubscribeControl()
    {
        _control = null;
        _topLevel = null;
        _stretchSubscription?.Dispose();
        _stretchSubscription = null;
    }
}
