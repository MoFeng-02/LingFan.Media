namespace LingFan.Media.Outputs.OpenSLES;

/// <summary>
/// OpenSL ES 音频输出 DI 注册扩展方法（Android，V2-17 / O4）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddOpenSlesOutput()</code></para>
/// <para>注册的是工厂（Singleton），不是实例！<see cref="OpenSlesOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class OpenSlesExtensions
{
    /// <summary>
    /// 注册 OpenSL ES 音频输出（Android，AAudio 的低版本回退）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <exception cref="PlatformNotSupportedException">非 Android 平台调用。</exception>
    public static MediaBuilder AddOpenSlesOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("OpenSL ES 输出仅支持 Android。");

        builder.Services.AddSingleton<IAudioOutputFactory, OpenSlesOutputFactory>();
        return builder;
    }
}
