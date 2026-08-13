using LingFan.Media.Renderers.Shared;
using Vortice.Direct3D11;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// <see cref="ISharedGpuSurfaceSource"/> 的 D3D11 实现工厂（中立契约的 D3D11 适配器入口）。
/// </summary>
/// <remarks>
/// <para>作为「GPU 适配层」的一部分注册到 DI：UI 层（CompositionVideoRenderer）遍历
/// <c>IEnumerable&lt;ISharedGpuSurfaceSourceFactory&gt;</c>，选中首个 <see cref="IsAvailable"/>
/// 且句柄类型被宿主合成器支持的工厂，从而 UI 层不含任何「优先 D3D11」硬编码分支。</para>
/// <para>产出句柄类型固定为 <see cref="SharedGpuHandleKind.D3D11TextureGlobalSharedHandle"/>——
/// 与 Avalonia GpuInterop 官方样例的 legacy 全局共享句柄路径一致。</para>
/// <para><see cref="IsAvailable"/> 为轻量平台判定（Windows），不触碰原生资源；
/// 真正的设备/纹理创建延迟到 <see cref="Create"/>（若共享 D3D11 设备尚未就绪则在此创建）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class D3D11SharedSurfaceSourceFactory : ISharedGpuSurfaceSourceFactory
{
    private readonly D3D11RendererFactory _rendererFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="D3D11SharedSurfaceSourceFactory"/> 的新实例。
    /// </summary>
    /// <param name="rendererFactory">D3D11 渲染器工厂（持有共享 D3D11 设备）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public D3D11SharedSurfaceSourceFactory(D3D11RendererFactory rendererFactory, ILoggerFactory loggerFactory)
    {
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind => SharedGpuHandleKind.D3D11TextureGlobalSharedHandle;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">当前环境无法创建 D3D11 共享设备时（调用方应回退下一个工厂）。</exception>
    public ISharedGpuSurfaceSource Create(SharedGpuAdapterIdentity? targetAdapter = null)
    {
        // 触发共享设备延迟创建（若解码器尚未创建，则在此创建；已创建则返回缓存实例）。
        // RenderContext.SharedDevice 即工厂持有的真实 ID3D11Device（非新包装，无引用计数游戏）。
        RenderContext ctx = _rendererFactory.Context;
        if (ctx.GpuApiType != GPUApiType.D3D11 || ctx.SharedDevice is not ID3D11Device device)
            throw new NotSupportedException("D3D11 共享表面源需要已初始化的 D3D11 共享设备（当前不可用）。");

        // 共享设备已开启多线程保护；使用其 immediate context 提交（与管线/解码线程并发安全）。
        ID3D11DeviceContext d3d11Context = device.ImmediateContext;
        var source = new D3D11SharedSurfaceSource(
            device, d3d11Context, _loggerFactory.CreateLogger<D3D11SharedSurfaceSource>());
        // 宽高比缩放模式与渲染器工厂保持一致（默认 Uniform 信箱）
        source.ScaleMode = _rendererFactory.ScaleMode;
        return source;
    }
}
