namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// OpenAL 音频输出 DI 注册扩展方法（桩）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddOpenALOutput()</code></para>
/// <para>注册 <see cref="OpenALOutputFactory"/> 为 Singleton。
/// 调用 <c>Create()</c> 返回的 <see cref="OpenALOutput"/> 在使用时抛出 <see cref="NotSupportedException"/>。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class OpenALExtensions
{
    /// <summary>
    /// 注册 OpenAL 音频输出（桩——尚未实现）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    public static MediaBuilder AddOpenALOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IAudioOutputFactory, OpenALOutputFactory>();
        return builder;
    }
}
