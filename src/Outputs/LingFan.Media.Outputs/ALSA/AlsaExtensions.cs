namespace LingFan.Media.Outputs.Alsa;

/// <summary>
/// ALSA 音频输出 DI 注册扩展方法（不支持，注册期即抛）。
/// </summary>
/// <remarks>
/// <para>使用模式：<code>services.AddLingFanMedia().AddAlsaOutput()</code></para>
/// <para>ALSA <b>不在本库范围内</b>（Linux 音频由 OpenAL 覆盖）。与 GStreamer/WebRTC 同口径，
/// 统一为<b>注册期 fail-fast</b>：调用立即抛 <see cref="NotSupportedException"/>，
/// 不做「注册成功、运行时才抛」的延迟失败——消费方在装配阶段即得知不支持，而非播放中途。</para>
/// <para>此方法为同步配置，无 I/O。</para>
/// </remarks>
public static class AlsaExtensions
{
    /// <summary>
    /// 注册 ALSA 音频输出（不支持，注册期即抛）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>不返回（恒抛异常）。</returns>
    /// <exception cref="NotSupportedException">ALSA 输出不在当前范围。</exception>
    public static MediaBuilder AddAlsaOutput(this MediaBuilder builder)
    {
        throw new NotSupportedException(
            "ALSA 音频输出不在当前范围。Linux 上请使用 OpenAL（AddOpenALOutput）。");
    }
}