namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频输出 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddWasapiOutput()</code></para>
/// <para>注册的是工厂（Singleton），不是实例！WasapiOutput 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class WasapiExtensions
{
    /// <summary>
    /// 注册 WASAPI 音频输出（Windows）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <param name="configure">WASAPI 配置委托（可选）。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddWasapiOutput(
        this MediaBuilder builder,
        Action<WasapiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WASAPI 输出仅支持 Windows。");

        var options = new WasapiOptions();
        configure?.Invoke(options);

        // 注册 WasapiOptions（Singleton，供工厂构造注入）
        builder.Services.AddSingleton(options);

        // 注册工厂（Singleton，DI 自动注入 WasapiOptions + ILoggerFactory）
        builder.Services.AddSingleton<IAudioOutputFactory, WasapiOutputFactory>();

        return builder;
    }
}
