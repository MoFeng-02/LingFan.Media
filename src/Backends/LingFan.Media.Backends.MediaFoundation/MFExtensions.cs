using LingFan.Media.Backends.MediaFoundation.Decoders;
using LingFan.Media.Backends.MediaFoundation.Demuxer;
using LingFan.Media.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// MediaFoundation 后端 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddMediaFoundation(options => { ... })</code></para>
/// <para>注册的是工厂（Singleton），不是实例！Demuxer/Decoder 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>MFBackend 作为 Singleton 是安全的——只持有 MFStartup 全局状态。</para>
/// <para><b>仅 Windows 可用</b>：MFBackend 构造时检测平台，非 Windows 抛 PlatformNotSupportedException。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// <para>从 BackendStubs.cs 迁移真实实现。</para>
/// </remarks>
public static class MFExtensions
{
    /// <summary>
    /// 注册 MediaFoundation 后端（Demuxer + VideoDecoder + AudioDecoder）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">MediaFoundation 配置回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddMediaFoundation(
        this MediaBuilder builder,
        Action<MediaFoundationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MediaFoundationOptions();
        configure?.Invoke(options);

        // 注册 MF 后端入口（Singleton，持有 MFStartup 全局状态）
        builder.Services.AddSingleton<MFBackend>();
        builder.Services.AddSingleton(options);

        // 注册工厂（集合注册 TryAddEnumerable：支持多后端并存、按 DI 注册顺序参与运行时回退）
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMediaDemuxerFactory, MFDemuxerFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoDecoderFactory, MFVideoDecoderFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAudioDecoderFactory, MFAudioDecoderFactory>());

        // ── 无头 DXVA 设备自备（依赖倒置关键）──
        // IGpuDeviceContext 是 Abstractions 中立契约：有头模式由 AddD3D11Renderer / GPU Presenter 注册并胜出
        // （解码器与渲染器同设备 → 零拷贝）；无头模式（AddHeadlessRenderer 不注册）由 MF 自备窗口无关 D3D11 设备，
        // 供 DXVA 硬解。TryAdd 确保不覆盖已有注册（有头复用渲染器设备）。MF 与渲染器互不引用，仅经契约协作。
        builder.Services.TryAddSingleton<IGpuDeviceContext, MfGpuDeviceContext>();

        // ── SourceReader 自带硬解所需的 DXGI 设备管理器（A 方案）──
        // 进程级共享单例：绑定 IGpuDeviceContext 的 D3D11 设备，供 MFDemuxer 在创建 SourceReader 时
        // 以 MF_SOURCE_READER_D3D_MANAGER 挂载 ⇒ ReadSample 直接吐 DXGI 纹理样本（零拷贝）。
        // 构造期不触碰原生（开箱即用原则）：设备/管理器均在首次 TryGetManager() 时延迟创建。
        builder.Services.TryAddSingleton<MfDxgiDeviceManagerProvider>();

        return builder;
    }
}
