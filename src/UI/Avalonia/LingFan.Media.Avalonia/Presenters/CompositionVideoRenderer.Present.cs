using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

// CompositionVideoRenderer 的帧呈现职责段（部分类，按职责拆分，编译期等价）。
// Present（管线线程写共享纹理）+ PresentUi（UI 线程导入上屏，按同步模型分派）；
// Attach/Resolve/Dispose 在主文件，导入在 Import 段。
internal sealed partial class CompositionVideoRenderer
{
    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        if (_disposed || _source is null || _interop is null || _drawingSurface is null || _handleType is null)
        {
            PostNoteSkip();
            return;
        }

        // (1) 管线线程：把解码帧 GPU 内容渲染进独立的共享 D3D11 纹理（keyed mutex 握手）。
        //    必须在 frame 仍存活时（本线程，Emit 的 ReturnFrame 之前）完成——共享纹理内容被固化拷贝，
        //    与解码帧纹理解耦，故后续封送到 UI 线程导入时 frame 是否已释放均安全。
        //    共享 D3D11 设备已开启多线程保护，跨线程调用安全。
        if (!_source.TryWriteFrame(frame, out var desc) || !desc.IsValid)
        {
            PostNoteSkip();
            return;
        }

        // (2) 导入 + 上屏必须在 UI 线程（Compositor 拥有者）：Avalonia 的 ImportImage /
        //    UpdateWithKeyedMutexAsync / Visual 属性全部要求 UI 线程，否则抛 VerifyAccess 异常
        //    （之前在管线线程直接调用即暴露为「连续 30 帧无法呈现 → 回退 Skia」）。
        //    desc 为值类型，拷贝后跨线程安全传递到 UI 线程。
        var d = desc;
        Dispatcher.UIThread.Post(() => { _ = PresentUi(d); });
    }

    /// <summary>在 UI（Compositor 拥有者）线程执行共享纹理导入与上屏。</summary>
    private async Task PresentUi(SharedGpuSurfaceDescriptor desc)
    {
        // Detach 后可能有挂起的封送调用：字段已被 DetachCore 置空，直接退出避免触碰已释放对象。
        if (_disposed || _source is null || _interop is null || _compositor is null || _drawingSurface is null || _handleType is null)
        {
            PostNoteSkip();
            return;
        }

        try
        {
            // Android（SyncMode.None）路径：合成器 RenderInterface.Value 跨帧不稳定，而 Avalonia 的
            // CompositionImportedGpuImage.IsUsable 要求「导入时捕获的 Context」与「呈现时
            // Compositor.Server.RenderInterface.Value」为同一实例（Avalonia 12.1.1 源码证实：
            // Context 在 CompositionInterop 构造时一次性冻结）。跨帧缓存导入会因上下文重建而永久失配
            // （PlatformGraphicsContextLostException）。故每帧重新解析 interop（捕获当前 Value）+ 重新
            // 导入，并在呈现前 await ImportCompleted 确保服务端导入完成、Context 与 Value 一致。
            // 非 None 路径（Windows KeyedMutex / Linux Semaphores）上下文稳定，沿用缓存导入。
            ICompositionImportedGpuImage imported;
            if (_source.SyncMode == SharedGpuSyncMode.None)
            {
                var fresh = await ImportFreshAsync(desc).ConfigureAwait(true);
                if (fresh is null)
                {
                    PostNoteSkip();
                    return;
                }
                var old = _imported;
                _imported = fresh;
                old?.DisposeAsync();
                imported = fresh;
            }
            else
            {
                if (!await EnsureInteropAndImportAsync(desc).ConfigureAwait(true))
                    return;
                imported = _imported!;
            }

            // 信号量（仅 Semaphores 模型）：与源同生命周期，导入一次（非每帧），供 UpdateWithSemaphoresAsync 使用。
            if (_source.SyncMode == SharedGpuSyncMode.Semaphores && _waitSem is null)
            {
                var sem = _source.Semaphores;
                if (sem is not { IsValid: true })
                {
                    _logger.LogWarning("CompositionVideoRenderer Semaphores 模型但信号量无效，跳过本帧。");
                    PostNoteSkip();
                    return;
                }
                string semType = MapSemaphoreKind(sem.Value.Kind);
                _waitSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerWaitHandle, semType));
                _signalSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerSignalHandle, semType));
                if (_waitSem is null || _signalSem is null)
                {
                    _logger.LogWarning("CompositionVideoRenderer 信号量导入失败（句柄类型={SemType}），跳过本帧。", semType);
                    _waitSem?.DisposeAsync();
                    _signalSem?.DisposeAsync();
                    _waitSem = null;
                    _signalSem = null;
                    PostNoteSkip();
                    return;
                }
            }

            // 帧尺寸变化或首次呈现时，按 VideoView.Stretch 重新计算子视觉目标尺寸与偏移。
            // 必须在 UpdateWithKeyedMutexAsync 之前完成，确保合成器采样到正确布局。
            var newFrameSize = new Vector(desc.Width, desc.Height);
            if (_frameSize != newFrameSize)
            {
                _frameSize = newFrameSize;
                UpdateSurfaceLayout();
            }

            // Size=0 兜底：若子视觉尺寸仍为 0（Attach 时控件布局尚未完成、SizeChanged 尚未触发），
            // 按控件当前边界重设，避免「内容已提交但显示尺寸为 0 的空白」。
            if (_surfaceVisual is { } sv && (sv.Size.X == 0 || sv.Size.Y == 0) && _attachedVisual is { } v)
            {
                _controlSize = new Vector(v.Bounds.Width, v.Bounds.Height);
                UpdateSurfaceLayout();
            }

            _surfaceVisual!.Opacity = 1;

            // 按源的同步模型选择提交方式：各后端只用自己 API 的原生机制，互不跨界、不伪造句柄。
            Task present = _source.SyncMode == SharedGpuSyncMode.Semaphores && _waitSem is not null && _signalSem is not null
                ? _drawingSurface.UpdateWithSemaphoresAsync(imported, _waitSem, _signalSem)
                : _source.SyncMode == SharedGpuSyncMode.None
                    // 无显式同步（Android）：由 Avalonia 合成器平台层（UpdateAsync）自管跨端同步，
                    // 走 UpdateWithAutomaticSync 路径（sync=None）。
                    ? _drawingSurface.UpdateAsync(imported)
                    // 消费者（Avalonia 合成线程）以 ConsumerAcquireKey 取锁采样、以 ConsumerReleaseKey 归还。
                    : _drawingSurface.UpdateWithKeyedMutexAsync(
                        imported, (uint)_source.ConsumerAcquireKey, (uint)_source.ConsumerReleaseKey);

            // 上屏任务必须被健康监控观测：任务成功完成才记成功；任务失败/取消记一次跳过，
            // 连续达阈值即触发回退。否则「写入共享纹理成功但合成器从未采样 / 导入失效」会被误判为成功，
            // 连续成功计数永不归零 → Unhealthy 永不触发 → 永不回退 Skia → 永久空白且无任何日志。
            _lastPresent = present;
            _ = present.ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    // 把 AggregateException 展开记录，使合成器对导入 AHB/OPAQUE_FD 图像的
                    // 真实拒绝原因可见（静默吞掉异常会让「UpdateAsync 为何失败」无从定位）。
                    if (t.Exception is { } ex)
                        _logger.LogError(ex, "CompositionVideoRenderer UpdateAsync 上屏失败（帧跳过，累计将触发回退）。HandleType={HandleType} Version={Version}",
                            _handleType, _lastVersion);
                    // Android 路径每帧重新解析 interop+重导（ImportFreshAsync），故此处无需置 _interopStale；
                    // 下一帧会自动以当前 RenderInterface.Value 重新导入。仅记一次跳过，连续达阈值即触发回退。
                    PostNoteSkip();
                }
                else
                    NoteSuccess();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // 单帧提交异常（如跨设备导入失败）吞掉，绝不击杀管线线程；下一帧重试。
            // 用 Warning 而非 Trace：跨设备纹理导入失败是「无空域上屏空白」的首要嫌疑，必须可见以便诊断。
            _logger.LogWarning(ex, "CompositionVideoRenderer 提交帧失败（跳过本帧）。");
            PostNoteSkip();
        }
    }
}
