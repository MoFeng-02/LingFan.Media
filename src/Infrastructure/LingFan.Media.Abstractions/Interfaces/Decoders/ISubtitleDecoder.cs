namespace LingFan.Media.Abstractions;

/// <summary>
/// 字幕解码器接口。
/// </summary>
/// <remarks>
/// <para>不需要参数化 Initialize 方法——编解码信息由工厂 Create(MediaTrack) 时</para>
/// <para>通过 track.SubtitleCodec 预置到解码器内部。</para>
/// <para>生命周期由 InitializeAsync / Dispose / DisposeAsync 管理（继承自 IMediaComponent）。</para>
/// </remarks>
public interface ISubtitleDecoder : IMediaComponent
{
    /// <summary>解码一个字幕数据包。无 CancellationToken（热路径）。</summary>
    ValueTask<SubtitleFrame?> DecodeAsync(MediaPacket packet);

    /// <summary>刷新内部缓冲，取出剩余字幕。</summary>
    ValueTask<SubtitleFrame?> FlushAsync();

    /// <summary>重置解码器状态（Seek 后调用，清空字幕内部缓冲）。</summary>
    void Reset();

    /// <summary>字幕编解码器（由工厂 Create 时预置）。</summary>
    SubtitleCodec SubtitleCodec { get; }
}
