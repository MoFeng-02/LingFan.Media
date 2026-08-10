using LingFan.Media.Abstractions;
using LingFan.Media.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// VLC Native 后端 DI 注册扩展（零 LibVLCSharp，Apache-2.0 P/Invoke）。
/// </summary>
/// <remarks>
/// <para>注册：<see cref="VLCNativeBackend"/>(Singleton) + <see cref="VLCOptions"/>(Singleton，共享层) +
/// <see cref="VLCNativeDemuxerFactory"/>(Scoped，枚举注册为 IMediaDemuxerFactory)。</para>
/// <para>与原 LibVLCSharp 后端（AddVLC）并行存在，待全链路收口后下掉老后端。</para>
/// <para>🔴 后端/工厂绑定到 VLCNative 特有类型，不进共享层（两后端各持一份工厂+扩展）。</para>
/// </remarks>
public static class VLCNativeExtensions
{
    /// <summary>
    /// 注册 VLC Native 解封装后端（自写 P/Invoke，零 LibVLCSharp）。
    /// </summary>
    public static IServiceCollection AddVLCNative(this IServiceCollection services)
    {
        // 🔴 Lazy&lt;T&gt; 解析支持必须显式开启（MS DI 不自动支持 Lazy&lt;VLCNativeBackend&gt;）。
        services.AddLazySupport();

        services.TryAddSingleton<VLCOptions>();
        services.TryAddSingleton<VLCNativeBackend>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMediaDemuxerFactory, VLCNativeDemuxerFactory>());

        return services;
    }
}
