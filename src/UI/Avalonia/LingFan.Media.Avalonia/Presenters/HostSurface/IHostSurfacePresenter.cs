using System;
using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 宿主表面呈现适配器（渲染器骨架与包装方式之间的原子插拔单元）：把
/// <see cref="SharedGpuSurfaceDescriptor"/> 描述的共享 GPU 表面包装为当前宿主 Skia 上下文
/// 可采样的图像。每个实现对应「宿主渲染后端 × 表面句柄类型」的一种组合。
/// </summary>
/// <remarks>
/// <para><b>原子化边界</b>：渲染器骨架（帧写入 / 快照交接 / 健康计数 / 几何拉伸）与
/// 「如何把共享表面包装为可采样图像」彻底解耦——新增宿主/句柄组合 = 新增一个实现并注册 DI，
/// 渲染器骨架零改动；实现之间互不知道对方。</para>
/// <para><b>能力自报</b>：<see cref="CanPresent"/> 声明本适配器可处理的描述符形态；
/// 调用方按 DI 注册顺序取第一个匹配者，骨架不硬编码任何优先级。</para>
/// <para><b>线程</b>：实现必须无状态（可并发调用）；<see cref="TryWrap"/> 全部发生在
/// Avalonia 渲染线程的 SkiaSharp lease 作用域内，为同步调用（纯 GPU 包装，无 I/O，补 async 即伪异步）。</para>
/// <para><b>生命周期</b>：包装产物仅在当前绘制命令内有效，调用方绘制后立即释放；
/// 底层共享表面（VkImage/VkDeviceMemory 等）归表面源所有，实现只借用、绝不释放。</para>
/// <para><b>范围说明</b>：本契约覆盖「Skia 通道家族」（Vulkan 纹理 / GL 纹理 / CPU 图像等
/// Skia 可采样形态）；Composition 导入通道生命周期不同（SurfaceVisual + UpdateAsync），
/// 由独立渲染器经既有 <c>IVideoRenderer</c> 级插拔覆盖。</para>
/// <para><b>AOT 兼容</b>：接口 + sealed 无状态实现，零反射。</para>
/// </remarks>
public interface IHostSurfacePresenter
{
    /// <summary>能力自报：本适配器能否呈现该描述符（句柄类型等运行时特征）。</summary>
    bool CanPresent(in SharedGpuSurfaceDescriptor descriptor);

    /// <summary>
    /// 在 SkiaSharp lease 作用域内把共享表面包装为可采样图像（Avalonia 渲染线程）。
    /// </summary>
    /// <param name="grContext">宿主 Skia GPU 上下文。</param>
    /// <param name="descriptor">共享表面描述符（只读借用）。</param>
    /// <param name="surface">成功时的包装产物（图像 + 关联后端纹理包装），调用方绘制后释放。</param>
    /// <param name="failureReason">失败原因（成功时为 <see langword="null"/>），由调用方计入失败统计。</param>
    /// <returns><see langword="true"/> = 包装成功；<see langword="false"/> = 本帧无法呈现（调用方丢弃，不打断管线）。</returns>
    bool TryWrap(
        SkiaSharp.GRContext grContext,
        in SharedGpuSurfaceDescriptor descriptor,
        [NotNullWhen(true)] out SkiaWrappedSurface? surface,
        out string? failureReason);
}

/// <summary>
/// 一次包装的产物：<see cref="Image"/> 为可采样图像，<see cref="BackendTexture"/> 为其关联的
/// 后端纹理包装（部分宿主组合存在，Vulkan 组合为 <see cref="SkiaSharp.GRBackendTexture"/>）。
/// 调用方绘制完成后 <see cref="Dispose"/>（先图像后纹理包装及扩展释放）；底层共享表面不受影响。
/// </summary>
public sealed class SkiaWrappedSurface : IDisposable
{
    private readonly SkiaSharp.GRBackendTexture? _backendTexture;
    private readonly Action? _extraRelease;

    /// <summary>创建包装产物。</summary>
    /// <param name="image">可采样图像（仅当前绘制命令内有效）。</param>
    /// <param name="backendTexture">关联后端纹理包装（可空）。</param>
    /// <param name="extraRelease">扩展释放回调（可空；如 GL 纹理对象名删除），在图像之后执行。</param>
    public SkiaWrappedSurface(SkiaSharp.SKImage image, SkiaSharp.GRBackendTexture? backendTexture, Action? extraRelease = null)
    {
        Image = image;
        _backendTexture = backendTexture;
        _extraRelease = extraRelease;
    }

    /// <summary>可采样图像（仅当前绘制命令内有效）。</summary>
    public SkiaSharp.SKImage Image { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Image.Dispose();
        _backendTexture?.Dispose();
        _extraRelease?.Invoke();
    }
}
