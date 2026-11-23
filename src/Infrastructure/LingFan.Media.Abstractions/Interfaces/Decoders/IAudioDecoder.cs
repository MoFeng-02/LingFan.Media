namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频解码器接口。
/// </summary>
/// <remarks>单线程使用（管线线程），非线程安全。热路径方法无 CancellationToken。</remarks>
public interface IAudioDecoder : IMediaComponent
{
    /// <summary>参数化配置：查找编解码器并打开。</summary>
    /// <param name="codec">音频编解码器。</param>
    /// <param name="settings">音频配置（含流时间基与可选的重采样目标格式）。</param>
    void Initialize(AudioCodec codec, AudioSettings settings);

    /// <summary>解码一个数据包。无 CancellationToken（热路径）。</summary>
    /// <param name="packet">待解码的数据包。</param>
    /// <returns>
    /// 解码出的音频帧；返回 <see langword="null"/> 表示<b>本次未产出音频帧</b>
    /// （需要更多数据、流已到尾，或该包解码失败可跳过），<b>不代表</b>解码器失效——
    /// 调用方不得据此判定整体失败或丢弃解码器（与 <see cref="IVideoDecoder.DecodeAsync"/> 的 null 语义一致）。
    /// </returns>
    ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet);

    /// <summary>刷新内部缓冲，取出剩余帧。</summary>
    /// <returns>缓冲中剩余的一个音频帧；返回 <see langword="null"/> 表示缓冲已取尽。</returns>
    ValueTask<AudioFrame?> FlushAsync();

    /// <summary>重置解码器状态（Seek 后调用）。</summary>
    void Reset();

    /// <summary>当前编解码器。</summary>
    AudioCodec Codec { get; }

    /// <summary>解码器实际输出采样率（源采样率，或重采样目标采样率）。</summary>
    /// <remarks>
    /// <para>由 <c>Initialize</c> 建立，MediaPlayer 据此初始化音频输出设备（WASAPI 以固定率开设备）。</para>
    /// <para><b>时序</b>：<c>Initialize</c> 返回后本值<b>允许为 0</b>，表示此刻尚无法从码流判定
    /// （如 AAC/MP3 需先读到首个数据包才能确定采样率）。0 是合法状态，不是错误码、不代表解码失败，
    /// 也不得据此抛异常。</para>
    /// <para><b>实现义务</b>：必须在<b>首个音频帧产出前</b>（即首次 <see cref="DecodeAsync"/> 返回
    /// 非 null 帧之前）把本值回填为正。回填仍未发生就交出音频帧，属于实现违约。</para>
    /// <para><b>消费方义务</b>：在 <see cref="Initialize"/> 返回后读到 0 时<b>必须</b>自行延迟初始化
    /// 输出设备，待首个音频帧到达、本值确定后再初始化——把 0 直接交给输出设备会触发
    /// <c>ArgumentOutOfRangeException</c>（输出设备必须以合法固定率打开）。</para>
    /// </remarks>
    int OutputSampleRate { get; }

    /// <summary>解码器实际输出声道数（源声道数，或重采样目标声道数）。</summary>
    /// <remarks>
    /// <para>时序与 <see cref="OutputSampleRate"/> 完全相同：<see cref="Initialize"/> 返回后允许为 0
    /// （此刻尚不可判定，0 是合法状态），必须在首个音频帧产出前回填为正；消费方读到 0 时必须
    /// 自行延迟初始化输出设备，不得据此判定失败。</para>
    /// </remarks>
    int OutputChannels { get; }
}
