namespace LingFan.Media.Audio;

/// <summary>
/// 音频效果接口。对 <see cref="AudioFrame"/> 执行单帧变换。
/// </summary>
/// <remarks>
/// <para><b>所有权转移语义</b>：<see cref="Process"/> 接收输入帧的所有权，
/// 调用后输入帧被 Dispose（释放底层 buffer），返回新的 <see cref="AudioFrame"/>。</para>
/// <para>当 <see cref="IsEnabled"/> 为 false 时，<see cref="Process"/> 直接返回输入帧，
/// 不 Dispose、不创建新帧（透传）。</para>
/// <para>实现应为 sealed 类以保证 AOT 友好。</para>
/// <para>参数使用强类型 <see cref="AudioEffectParameter"/>（不用 <c>Dictionary&lt;string, object&gt;</c>）。</para>
/// </remarks>
public interface IAudioEffect
{
    /// <summary>效果名称。</summary>
    string Name { get; }

    /// <summary>是否启用。禁用时 <see cref="Process"/> 透传输入帧。</summary>
    bool IsEnabled { get; set; }

    /// <summary>效果参数列表（强类型，AOT 友好）。</summary>
    IReadOnlyList<AudioEffectParameter> Parameters { get; }

    /// <summary>
    /// 处理一帧音频。
    /// </summary>
    /// <param name="frame">输入帧（所有权转移：处理后输入帧被 Dispose）。</param>
    /// <returns>处理后的新帧（或禁用时返回原帧）。</returns>
    /// <remarks>
    /// 所有权转移：输入 frame 被 Dispose，返回新 frame 传入下一个效果。
    /// 当 <see cref="IsEnabled"/> 为 false 时直接返回输入帧（不 Dispose，不创建新帧）。
    /// </remarks>
    AudioFrame Process(AudioFrame frame);
}
