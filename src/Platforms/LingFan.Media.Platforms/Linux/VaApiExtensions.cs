using LingFan.Media.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LingFan.Media.Platforms.Linux;

/// <summary>
/// Linux 平台 DI 扩展：注册 VAAPI 零拷贝导出（<see cref="IVaApiExport"/> → <see cref="VaApiInterop"/>）。
/// </summary>
/// <remarks>
/// 仅在 Linux 调用（探针在 <c>OperatingSystem.IsLinux()</c> 分支中调用）。注册后，
/// <see cref="LingFan.Media.Backends.FFmpeg.Decoders.FFmpegVideoDecoder"/> 在 <c>--hw</c> 且 Linux 时
/// 经此抽象把 VAAPI 表面导出为 dma_buf，实现真实零拷贝硬解。Windows/macOS 不注册，解码器回落软解。
/// </remarks>
public static class VaApiExtensions
{
    /// <summary>注册 <see cref="IVaApiExport"/> → <see cref="VaApiInterop"/>（Singleton）。</summary>
    public static IServiceCollection AddVaApi(this IServiceCollection services)
    {
        services.Add(new ServiceDescriptor(typeof(IVaApiExport), typeof(VaApiInterop), ServiceLifetime.Singleton));
        return services;
    }
}
