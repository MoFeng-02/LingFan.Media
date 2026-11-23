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

// CompositionVideoRenderer 的互操作解析职责段（部分类，按职责拆分，编译期等价）。
// UI 线程解析 Compositor 互操作与句柄类型（server job 调度）；Attach/Present/Dispose 在主文件。
internal sealed partial class CompositionVideoRenderer
{
    /// <summary>
    /// 延迟解析 GPU 互操作并选定共享表面源。在 UI 线程、渲染循环已启动后由
    /// <see cref="Attach"/> 经 <see cref="Dispatcher"/> 调度执行；解析失败即触发
    /// <see cref="Unhealthy"/> 让宿主回退 Skia。
    /// </summary>
    private async Task ResolveAsync()
    {
        if (_disposed || _compositor is null || _attachedVisual is not Visual visual)
            return;

        // UI 线程 + await（非阻塞）：渲染循环已启动，server job 可被调度执行，无死锁；
        // continuation 经 ConfigureAwait(true) 回到 UI 线程，后续 ImportImage 等合成器操作满足 VerifyAccess。
        ICompositionGpuInterop? interop;
        try
        {
            interop = await _compositor.TryGetCompositionGpuInterop().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionVideoRenderer 解析 GPU 互操作失败，回退 Skia。");
            MarkUnhealthy();
            return;
        }

        if (interop is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 当前 Avalonia 渲染后端不支持 ICompositionGpuInterop，回退 Skia。");
            MarkUnhealthy();
            return;
        }

        // 诊断：打印合成器实际支持的句柄类型集合，用于确认零拷贝路径是否被接受
        // （legacy 全局共享句柄不支持跨 GPU 适配器；若合成器在其他适配器上则导入会静默黑屏）。
        _logger.LogInformation(
            "CompositionVideoRenderer 合成器支持句柄类型=[{HandleTypes}]。",
            string.Join(", ", interop.SupportedImageHandleTypes));

        // _interop 须在导入自检（选择循环内调用）前就绪。
        _interop = interop;

        // 把宿主合成器所在的物理 GPU 身份透传给工厂，供生产者优选同一设备
        // （跨设备共享纹理/信号量导入要求生产者与合成器位于同一物理 GPU，否则静默黑屏）。
        ReadOnlyMemory<byte> uuidMem = interop.DeviceUuid is { Length: 16 } ? interop.DeviceUuid : default(ReadOnlyMemory<byte>);
        ReadOnlyMemory<byte> luidMem = interop.DeviceLuid is { Length: 8 } ? interop.DeviceLuid : default(ReadOnlyMemory<byte>);
        SharedGpuAdapterIdentity? identity = (uuidMem.IsEmpty && luidMem.IsEmpty)
            ? null
            : new SharedGpuAdapterIdentity(uuidMem, luidMem);

        // —— 选定共享表面源：逐工厂 Create + 导入自检，失败即跳过试下一个（Vulkan→D3D11→…→软渲）——
        // 进程级记忆（SharedGpuSurfaceSourceSelector）：命中缓存胜出厂优先尝试，避免每次从注册序头部
        // 逐个探测；缓存项本次失败则失效，强制下次全扫描。OpenGL 等未实现后端自然不在候选内。
        string selectionKey = BuildSelectionKey(interop);
        _selector.TryGet(selectionKey, _surfaceFactories, out var cached);
        var candidates = new List<ISharedGpuSurfaceSourceFactory>(_surfaceFactories);
        if (cached is not null && candidates.Contains(cached))
        {
            candidates.Remove(cached);
            candidates.Insert(0, cached);
        }

        ISharedGpuSurfaceSourceFactory? selected = null;
        ISharedGpuSurfaceSource? source = null;
        string? handleType = null;
        bool cachedFailed = false;

        foreach (var f in candidates)
        {
            if (!f.IsAvailable)
                continue;
            string? ht = MapHandleKind(f.HandleKind);
            if (ht is null || !interop.SupportedImageHandleTypes.Contains(ht))
            {
                // 句柄类型自修复：Android AHB 候选常量未命中时，从合成器实际支持的句柄类型中认领
                // 含 "HardwareBuffer"/"android" 的入口（运行时确认，不靠猜测）。认领成功即继续，
                // 失败则跳过本工厂（回退下一个 / Skia）。
                if (f.HandleKind == SharedGpuHandleKind.AndroidHardwareBuffer)
                    ht = interop.SupportedImageHandleTypes.FirstOrDefault(t =>
                        t.Contains("HardwareBuffer", StringComparison.OrdinalIgnoreCase) ||
                        t.Contains("android", StringComparison.OrdinalIgnoreCase));
                if (ht is null || !interop.SupportedImageHandleTypes.Contains(ht))
                {
                    // 跳过必须留痕：合成器句柄类型为空集时（典型：Android 跑在
                    // EGL 后端 —— Avalonia 的 GL 后端不实现外部图像导入，SupportedImageHandleTypes 为 []），
                    // 每个工厂都会走到这里；不留痕迹则最终只看到一句笼统的「无可用工厂」。
                    // 逐工厂打点：句柄类型 + 映射结果 + 合成器实际支持列表，一眼看出是后端选错还是映射缺项。
                    _logger.LogWarning(
                        "CompositionVideoRenderer 跳过工厂 {Factory}：句柄类型 {HandleKind}（映射={Mapped}）" +
                        "不在合成器支持列表内。合成器后端支持=[{Supported}]。" +
                        "若列表为空，多半是 Avalonia 渲染后端选错（Android 需 AndroidRenderingMode.Vulkan）。",
                        f.GetType().Name, f.HandleKind, ht ?? "<无法映射>",
                        string.Join(", ", interop.SupportedImageHandleTypes));
                    continue;
                }
                _logger.LogInformation("CompositionVideoRenderer 已自动认领 Android AHB 句柄类型={HandleType}。", ht);
            }

            ISharedGpuSurfaceSource? candidate = null;
            try
            {
                candidate = f.Create(identity);
                // 临时落字段，供循环内 SelfTestImport 读取；成功后保留。
                _source = candidate;
                _handleType = ht;
                if (!SelfTestImport())
                {
                    _logger.LogWarning(
                        "CompositionVideoRenderer 共享表面源工厂 {Factory} 导入自检失败，尝试下一个候选。",
                        f.GetType().Name);
                    if (cached is not null && ReferenceEquals(f, cached))
                        cachedFailed = true;
                    continue;
                }

                selected = f;
                source = candidate;
                handleType = ht;
                _selector.Record(selectionKey, f);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "CompositionVideoRenderer 共享表面源工厂 {Factory} 创建失败，尝试下一个候选。",
                    f.GetType().Name);
                if (cached is not null && ReferenceEquals(f, cached))
                    cachedFailed = true;
                continue;
            }
            finally
            {
                // 失败分支：释放候选并清空临时字段，避免残留指向已弃用源。
                if (selected is null)
                {
                    candidate?.Dispose();
                    _source = null;
                    _handleType = null;
                }
            }
        }

        if (cachedFailed)
            _selector.Invalidate(selectionKey);

        if (selected is null || source is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 无可用且被合成器支持的共享表面源工厂，回退 Skia。");
            MarkUnhealthy();
            return;
        }

        _logger.LogInformation(
            "CompositionVideoRenderer 选定共享表面源工厂={Factory}，句柄类型={HandleType}（缓存命中={Cached}）。",
            selected.GetType().Name, handleType, cached is not null && ReferenceEquals(selected, cached));

        // _source / _handleType 在成功分支落定，供后续 Present 导入上屏。
        _source = source;
        _handleType = handleType;

        _logger.LogInformation(
            "CompositionVideoRenderer 导入自检通过，启用零拷贝上屏路径（句柄类型={HandleType}）。",
            _handleType);
    }
}
