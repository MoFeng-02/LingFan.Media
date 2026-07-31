namespace LingFan.Media.Abstractions;

/// <summary>
/// 源音频格式感知接口。可选实现：<b>直通型</b>音频解码器（自身不做解码、由解封装层已产出 PCM）
/// 通过此接口从调用方接收真实的 PCM 输出格式。
/// </summary>
/// <remarks>
/// <para><b>动机</b>：<see cref="IAudioDecoder.OutputSampleRate"/> / <see cref="IAudioDecoder.OutputChannels"/>
/// 是 MediaPlayer 初始化音频输出设备（如 WASAPI 以固定采样率开设备）的唯一依据。
/// 自带解码器上下文的后端（FFmpeg）能从 codec context 直接得知源参数；
/// 而<b>直通型</b>后端（MediaFoundation：SourceReader 内部完成解码，解码器仅包装 PCM 字节）
/// 无从得知参数，若硬编码默认值（44100/2）会与真实媒体不符 → 音高/节奏错乱。
/// 本接口让调用方把解封装层解析出的真实格式注入进来。</para>
/// <para><b>不修改既有接口</b>（IAudioDecoder 等），由调用方 pattern matching（<c>is</c>）检测，
/// 与 <see cref="IFramePoolAware{T}"/> 同一范式；AOT 安全（编译期确定类型，无反射）。</para>
/// <para><b>调用时机</b>：必须在 <see cref="IAudioDecoder.Initialize"/> 之后、
/// 音频输出设备初始化与首次 <see cref="IAudioDecoder.DecodeAsync"/> 之前调用。</para>
/// <para><b>异步策略</b>：sync（config 分类）——纯内存赋值，无 I/O，不提供 async 重载。</para>
/// </remarks>
public interface IAudioSourceFormatAware
{
    /// <summary>
    /// 注入解封装层实测的 PCM 输出格式。
    /// </summary>
    /// <param name="sampleRate">采样率（Hz），必须 &gt; 0。</param>
    /// <param name="channels">声道数，必须 &gt; 0。</param>
    /// <param name="sampleFormat">采样格式。</param>
    void SetSourceFormat(int sampleRate, int channels, SampleFormat sampleFormat);
}
