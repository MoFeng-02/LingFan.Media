namespace LingFan.Media.Outputs.AAudio;

/// <summary>
/// AAudio 音频输出 DI 注册扩展方法（Android API 27+，O5）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddAAudioOutput()</code></para>
/// <para>注册的是工厂（Singleton），不是实例！<see cref="AAudioOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>低版本回退：AAudio 需 Android 8.1（API 27）+。低版本设备请由宿主改注册
/// <c>AddOpenSlesOutput()</c>（宿主侧可用 <c>OperatingSystem.IsAndroidVersionAtLeast(27)</c> 判定）。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class AAudioExtensions
{
    /// <summary>
    /// 注册 AAudio 音频输出（Android API 27+；低版本请注册 OpenSL ES）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <exception cref="PlatformNotSupportedException">非 Android 平台调用。</exception>
    public static MediaBuilder AddAAudioOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("AAudio 输出仅支持 Android（API 27+）。");

        builder.Services.AddSingleton<IAudioOutputFactory, AAudioOutputFactory>();
        return builder;
    }
}
