using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// OpenAL 音频输出 DI 注册扩展方法（跨平台回退输出，C 组 AUDIO-STUB 真实实现）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddOpenALOutput()</code></para>
/// <para>注册 <see cref="OpenALOutputFactory"/> 为 Singleton。OpenAL 真正跨平台（Windows/Linux/macOS/Android），
/// 无需 <see cref="OperatingSystem"/> 守卫；宿主须提供对应原生库（见 <see cref="OpenALInterop"/>）。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class OpenALExtensions
{
    /// <summary>
    /// 注册 OpenAL 音频输出（跨平台回退，C 组 AUDIO-STUB 真实实现）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    public static MediaBuilder AddOpenALOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IAudioOutputFactory, OpenALOutputFactory>();
        return builder;
    }
}
