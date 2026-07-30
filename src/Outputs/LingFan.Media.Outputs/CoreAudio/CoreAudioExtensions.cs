namespace LingFan.Media.Outputs.CoreAudio;

/// <summary>
/// CoreAudio 音频输出 DI 注册扩展方法（macOS，V2-18 / O2）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddCoreAudioOutput()</code></para>
/// <para>注册的是工厂（Singleton），不是实例！<see cref="CoreAudioOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class CoreAudioExtensions
{
    /// <summary>
    /// 注册 CoreAudio 音频输出（macOS）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <exception cref="PlatformNotSupportedException">非 macOS 平台调用。</exception>
    public static MediaBuilder AddCoreAudioOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("CoreAudio 输出仅支持 macOS。");

        builder.Services.AddSingleton<IAudioOutputFactory, CoreAudioOutputFactory>();
        return builder;
    }
}
