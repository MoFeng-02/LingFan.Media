namespace LingFan.Media.Outputs.AVAudioEngine;

/// <summary>
/// iOS RemoteIO 音频输出 DI 注册扩展方法（V2-18 / O3）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddAvAudioEngineOutput()</code></para>
/// <para>注册的是工厂（Singleton），不是实例！<see cref="AvAudioEngineOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>方法名保留 AVAudioEngine 字样以维持既有注册面不变；实现走 RemoteIO AudioUnit（用户 2026-07-28 拍板）。</para>
/// <para>此方法为同步配置（config 分类），无 I/O。</para>
/// </remarks>
public static class AvAudioEngineExtensions
{
    /// <summary>
    /// 注册 iOS 音频输出（RemoteIO AudioUnit）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <exception cref="PlatformNotSupportedException">非 iOS 平台调用。</exception>
    public static MediaBuilder AddAvAudioEngineOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException("AVAudioEngine(RemoteIO) 输出仅支持 iOS。");

        builder.Services.AddSingleton<IAudioOutputFactory, AvAudioEngineOutputFactory>();
        return builder;
    }
}
