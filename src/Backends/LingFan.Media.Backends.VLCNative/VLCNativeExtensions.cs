using System;
using LingFan.Media.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// VLC Native 后端 DI 注册扩展（零 LibVLCSharp，Apache-2.0 P/Invoke）。
/// </summary>
/// <remarks>
/// <para>注册：<see cref="VLCNativeBackend"/>(Singleton) + <see cref="VLCOptions"/>(Singleton，位于 VLCNative 根) +
/// <see cref="VLCNativeDemuxerFactory"/>(Singleton，枚举注册为 IMediaDemuxerFactory) +
/// <see cref="VLCVideoDecoderFactory"/>(Singleton，枚举注册为 IVideoDecoderFactory) +
/// <see cref="VLCAudioDecoderFactory"/>(Singleton，枚举注册为 IAudioDecoderFactory)。</para>
/// <para>VLCNative 为唯一 VLC 后端（自写 Apache-2.0 P/Invoke，零 LibVLCSharp / LGPL），已替代退役的 LibVLCSharp 旧后端。</para>
/// <para>🔴 后端/工厂绑定到 VLCNative 特有类型，不进共享层（VLCNative 自持工厂与扩展）。</para>
/// </remarks>
public static class VLCNativeExtensions
{
    /// <summary>
    /// 注册 VLC Native 解封装后端（自写 P/Invoke，零 LibVLCSharp）。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="options">预构建的 VLC 选项（可选）。传入则注册该实例；为 null 时注册默认 <see cref="VLCOptions"/> 单例。</param>
    /// <returns>服务集合（链式调用）。</returns>
    public static IServiceCollection AddVLCNative(this IServiceCollection services, VLCOptions? options = null)
    {
        // 🔴 Lazy&lt;T&gt; 解析支持必须显式开启（MS DI 不自动支持 Lazy&lt;VLCNativeBackend&gt;）。
        services.AddLazySupport();

        if (options is not null)
            services.AddSingleton(options);
        else
            services.TryAddSingleton<VLCOptions>();

        services.TryAddSingleton<VLCNativeBackend>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMediaDemuxerFactory, VLCNativeDemuxerFactory>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoDecoderFactory, VLCVideoDecoderFactory>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioDecoderFactory, VLCAudioDecoderFactory>());

        return services;
    }

    /// <summary>
    /// 注册 VLC Native 解封装后端（MediaBuilder fluent 重载，等价于 <c>services.AddVLCNative()</c>）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddVLCNative(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddVLCNative();
        return builder;
    }

    /// <summary>
    /// 注册 VLC Native 解封装后端并配置选项（MediaBuilder fluent 重载）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">VLC 选项回调（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddVLCNative(this MediaBuilder builder, Action<VLCOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new VLCOptions();
        configure?.Invoke(options);
        builder.Services.AddVLCNative(options);
        return builder;
    }
}
