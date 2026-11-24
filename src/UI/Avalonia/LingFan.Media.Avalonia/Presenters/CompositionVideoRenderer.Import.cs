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

// CompositionVideoRenderer 的 Import 职责段（部分类，由 CompositionVideoRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/PresentUi/Dispose）在主文件；DirectComposition GPU 互操作导入与表面布局。
internal sealed partial class CompositionVideoRenderer
{
    // 把生产者描述符携带的 SharedGpuSurfaceFormat 映射到合成器导入所需的 PlatformGraphicsExternalImageFormat。
    // Android 零拷贝路径生产者用 R8G8B8A8（AHB 唯一等价格式，B8G8R8A8 无 AHB 等价），须对应 R8G8B8A8UNorm，
    // 否则合成器以 BGRA 采样会致通道互换；Windows/Linux 仍走 B8G8R8A8UNorm（与 D3D/Vulkan 约定一致）。
    private static PlatformGraphicsExternalImageFormat ToExternalFormat(SharedGpuSurfaceFormat fmt) =>
        fmt == SharedGpuSurfaceFormat.R8G8B8A8UNorm
            ? PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
            : PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm;

    /// <summary>
    /// Android（SyncMode.None）专用：每帧重新解析 interop 并导入当前帧外部图像。
    /// 依据（Avalonia 12.1.1 源码）：CompositionImportedGpuImage 在导入时把
    /// <c>Context = compositor.Server.RenderInterface.Value</c> 一次性冻结；<c>CompositionImportedGpuImage.IsUsable</c>
    /// 要求呈现时 <c>RenderInterface.Value</c> 与该 Context 为同一实例。Android 合成器 RenderInterface
    /// 跨帧重建，跨帧缓存导入必失配（PlatformGraphicsContextLostException）。每帧重解析+重导+await
    /// <c>ICompositionGpuImportedObject.ImportCompleted</c>，保证呈现瞬间 Context 与 Value 一致。
    /// </summary>
    /// <param name="desc">共享表面描述符（句柄/尺寸/格式/互斥键）。</param>
    /// <returns>本帧导入图像；任一环节失败返回 <see langword="null"/>（调用方应跳过本帧）。</returns>
    private async Task<ICompositionImportedGpuImage?> ImportFreshAsync(SharedGpuSurfaceDescriptor desc)
    {
        var interop = await _compositor!.TryGetCompositionGpuInterop().ConfigureAwait(true);
        if (interop is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 获取 GPU 互操作失败（合成器未就绪），跳过本帧。");
            return null;
        }
        var handleType = ResolveHandleType(interop);
        if (handleType is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 合成器不支持任何 Vulkan/AHardwareBuffer 句柄类型，跳过本帧。");
            return null;
        }
        _handleType = handleType;
        var props = new PlatformGraphicsExternalImageProperties
        {
            Width = desc.Width,
            Height = desc.Height,
            Format = ToExternalFormat(desc.Format),
            // 【必须透传】OPAQUE_FD 不携带内存元数据，Avalonia 的
            // VulkanExternalObjectsFeature.ImportedImage.CreateMemory 会拿这两个值与它自己
            // vkGetImageMemoryRequirements(导入图像) 的结果做严格相等校验，不符即抛
            // "Invalid memory size"（漏传 → 每帧导入失败 → 整段播放不出画）。
            // MemorySize 必须是生产者侧 vkGetImageMemoryRequirements().size（含驱动 tile/对齐扩容），
            // 不是 w*h*4；MemoryOffset 恒为 0。
            MemorySize = desc.MemorySize,
            MemoryOffset = desc.MemoryOffset,
        };
        if (!_importPropsLogged)
        {
            _importPropsLogged = true;
            _logger.LogInformation(
                "CompositionVideoRenderer 首次导入参数: 句柄类型={HandleType} {W}x{H} 格式={Fmt} " +
                "MemorySize={MemSize} MemoryOffset={MemOff}（MemorySize=0 说明生产者未上报，导入必失败）",
                handleType, desc.Width, desc.Height, desc.Format, desc.MemorySize, desc.MemoryOffset);
        }
        var imported = interop.ImportImage(new PlatformHandle(desc.Handle, handleType), props);
        // 关键：await 服务端导入完成。若 Context 与当前 RenderInterface.Value 失配，导入阶段即抛
        // PlatformGraphicsContextLostException（ImportCompleted 变 Faulted），此处 await 直接抛出，
        // 由 PresentUi 的 try/catch 捕获并跳过本帧——比呈现时才发现失配更早、更明确。
        await imported.ImportCompleted.ConfigureAwait(true);
        return imported;
    }

    /// <summary>从合成器支持的句柄类型中选出本机可用者：优先 OpaqueFd（Vulkan 导出跨 GL/VK 通用），
    /// 其次 AHardwareBuffer / android 相关类型，最后兜底取第一个。</summary>
    private static string? ResolveHandleType(ICompositionGpuInterop interop)
    {
        var types = interop.SupportedImageHandleTypes;
        return types.FirstOrDefault(t => t.Contains("OpaqueFd", StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => t.Contains("HardwareBuffer", StringComparison.OrdinalIgnoreCase)
                                         || t.Contains("android", StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault();
    }

    /// <summary>
    /// 确保导入图像可用，处理两类重建场景：
    /// (1) 共享纹理重建（生产者 <see cref="SharedGpuSurfaceDescriptor.Version"/> 变化）→ 丢弃旧导入，按新句柄/尺寸重导；
    /// (2) 合成器 GPU 上下文重建（Android 初始化期 RenderInterface 被重建）→ 旧 interop 在创建时捕获的 Context 快照
    ///    与当前 <c>compositor.Server.RenderInterface.Value</c> 不再是同一实例，其导入图像
    ///    <c>IsUsable</c> 永久 false、<c>UpdateAsync</c> 抛 <see cref="PlatformGraphicsContextLostException"/>。
    ///    此时须重新 <c>TryGetCompositionGpuInterop()</c>（其构造函数会重新捕获当前 RenderInterface.Value 作为新 Context），
    ///    并丢弃旧导入重导。一次重拉即收敛（上下文已稳定）。
    /// 跨程序集无法直接读取 Avalonia 内部 <c>CompositionImportedGpuImage.IsUsable</c>，故由 <see cref="_interopStale"/>
    /// 标记驱动重拉（上屏续体在捕获 context-lost 异常时置位）。
    /// </summary>
    /// <returns>导入图像就绪可用返回 <see langword="true"/>；不可用（应跳过本帧或回退）返回 <see langword="false"/>。</returns>
    private async Task<bool> EnsureInteropAndImportAsync(SharedGpuSurfaceDescriptor desc)
    {
        // (2) 合成器上下文重建：重拉 interop 以刷新 Context 快照，并强制下方重导旧导入。
        if (_interopStale || _interop is null)
        {
            _interopStale = false;
            var interop = await _compositor!.TryGetCompositionGpuInterop().ConfigureAwait(true);
            if (interop is null)
            {
                _logger.LogWarning("CompositionVideoRenderer 重新解析 GPU 互操作失败，回退 Skia。");
                MarkUnhealthy();
                return false;
            }
            _interop = interop;

            // 句柄类型在新 interop 下仍应受支持（设备/驱动不变）；逐个认领逻辑同 Setup 选择循环。
            if (_handleType is null || !_interop.SupportedImageHandleTypes.Contains(_handleType))
            {
                _handleType = _interop.SupportedImageHandleTypes.FirstOrDefault(t =>
                    t.Contains("HardwareBuffer", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("android", StringComparison.OrdinalIgnoreCase));
            }
            if (_handleType is null || !_interop.SupportedImageHandleTypes.Contains(_handleType))
            {
                _logger.LogWarning("CompositionVideoRenderer 重解析后句柄类型不被合成器支持，回退 Skia。");
                MarkUnhealthy();
                return false;
            }

            // 旧导入基于失效 Context，必须丢弃，强制重导。
            _imported?.DisposeAsync();
            _imported = null;
            _lastVersion = 0;
            _waitSem?.DisposeAsync();
            _waitSem = null;
            _signalSem?.DisposeAsync();
            _signalSem = null;
        }

        // (1) 共享纹理重建或强制重导：按当前句柄/尺寸导入。
        if (_imported is null || _lastVersion != desc.Version)
        {
            _imported?.DisposeAsync();
            var props = new PlatformGraphicsExternalImageProperties
            {
                Width = desc.Width,
                Height = desc.Height,
                Format = ToExternalFormat(desc.Format),
                // 外部内存元数据必须如实透传：OPAQUE_FD / NT 句柄自身不携带内存尺寸，
                // 留 0 会被合成器以 "Invalid memory size" 拒绝（与 None 路径 ImportFreshAsync 同源）。
                MemorySize = desc.MemorySize,
                MemoryOffset = desc.MemoryOffset,
            };
            _imported = _interop.ImportImage(new PlatformHandle(desc.Handle, _handleType!), props);
            if (_imported is null)
            {
                _logger.LogWarning(
                    "CompositionVideoRenderer 导入共享纹理失败（句柄类型={HandleType}），跳过本帧。",
                    _handleType);
                return false;
            }
            _lastVersion = desc.Version;
        }
        return true;
    }

    /// <summary>
    /// 挂载期导入能力自检：用 1×1 软件探针帧走完整「写入共享纹理 → 合成器导入」路径，
    /// 验证本共享 D3D11 设备纹理能被 Avalonia 合成器跨设备导入。
    /// </summary>
    /// <returns>自检通过返回 <see langword="true"/>；任一环节失败返回 <see langword="false"/>。</returns>
    private bool SelfTestImport()
    {
        // 探针资源：VideoFrame / SoftwareFrameResource 仅实现 IDisposableFrame（非 System.IDisposable），
        // 故手动 new + finally 释放；导入图像为 IAsyncDisposable，与文件既有 _imported?.DisposeAsync() 一致。
        SoftwareFrameResource? probe = null;
        VideoFrame? frame = null;
        ICompositionImportedGpuImage? imported = null;
        ICompositionImportedGpuSemaphore? waitSem = null;
        ICompositionImportedGpuSemaphore? signalSem = null;
        try
        {
            // 1×1 BGRA 探针（4 字节）——仅验证导入可达性，尺寸任意。
            // 完全限定 PixelFormat 以消除与 Avalonia.Platform.PixelFormat 的歧义（CS0104/CS1503）。
            probe = new SoftwareFrameResource(1, 1, LingFan.Media.Abstractions.PixelFormat.BGRA32, new Memory<byte>(new byte[4]));
            frame = new VideoFrame(1, 1, LingFan.Media.Abstractions.PixelFormat.BGRA32, probe, TimeSpan.Zero, TimeSpan.Zero, true);
            if (!_source!.TryWriteFrame(frame, out var desc) || !desc.IsValid)
                return false;

            var props = new PlatformGraphicsExternalImageProperties
            {
                Width = desc.Width,
                Height = desc.Height,
                Format = ToExternalFormat(desc.Format),
                // 外部内存元数据必须如实透传：OPAQUE_FD / NT 句柄自身不携带内存尺寸，
                // 留 0 会被合成器以 "Invalid memory size" 拒绝（与 None 路径 ImportFreshAsync 同源）。
                MemorySize = desc.MemorySize,
                MemoryOffset = desc.MemoryOffset,
            };
            imported = _interop!.ImportImage(new PlatformHandle(desc.Handle, _handleType!), props);
            if (imported is null)
                return false;

            // Semaphores 模型：同步导入信号量，验证完整「写入 → 信号量 → 合成器导入」路径。
            if (_source.SyncMode == SharedGpuSyncMode.Semaphores)
            {
                var sem = _source.Semaphores;
                if (sem is not { IsValid: true })
                    return false;
                string semType = MapSemaphoreKind(sem.Value.Kind);
                waitSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerWaitHandle, semType));
                signalSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerSignalHandle, semType));
                if (waitSem is null || signalSem is null)
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionVideoRenderer 导入自检异常（回退 Skia）。");
            return false;
        }
        finally
        {
            // 级联释放探针 CPU 资源（释放顺序：先 VideoFrame 再其 Resource）。
            frame?.Dispose();
            probe?.Dispose();
            // 探针导入图像/信号量为一次性探测，释放即可；失败回退链不依赖这些导入实例。
            if (imported is not null)
                _ = imported.DisposeAsync();
            if (waitSem is not null)
                _ = waitSem.DisposeAsync();
            if (signalSem is not null)
                _ = signalSem.DisposeAsync();
        }
    }

    /// <summary>
    /// 构建选择记忆键：合成器支持的句柄类型集合 + 合成器所在 GPU 身份（UUID/LUID）。
    /// 同一机器同一合成器恒定命中；跨 GPU / 远程桌面合成器切换则自然重新探测。
    /// </summary>
    private static string BuildSelectionKey(ICompositionGpuInterop interop)
    {
        var sb = new System.Text.StringBuilder();
        var handles = interop.SupportedImageHandleTypes;
        if (handles is not null)
            foreach (var h in handles)
                sb.Append(h).Append(';');
        if (interop.DeviceUuid is { } uuid && uuid.Length == 16)
            sb.Append("U:").Append(Convert.ToHexString(uuid));
        if (interop.DeviceLuid is { } luid && luid.Length == 8)
            sb.Append("L:").Append(Convert.ToHexString(luid));
        return sb.ToString();
    }
}
