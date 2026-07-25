namespace LingFan.Media.Abstractions;

/// <summary>
/// 字幕解码器工厂接口。
/// </summary>
/// <remarks>
/// <para>与 IVideoDecoderFactory / IAudioDecoderFactory 对称：</para>
/// <para>工厂按流（MediaTrack）创建对应的 ISubtitleDecoder 实例。</para>
/// <para>Singleton 工厂，每次 Create() 返回新实例。</para>
/// <para>优先使用 <see cref="CreateAsync"/>（对称一致性，未来网络字幕加载 I/O，支持 CT）。</para>
/// </remarks>
public interface ISubtitleDecoderFactory
{
    /// <summary>根据字幕轨道创建解码器实例（按 track.SubtitleCodec 预置）。</summary>
    ISubtitleDecoder Create(MediaTrack track);

    /// <summary>异步根据字幕轨道创建解码器实例（按 track.SubtitleCodec 预置）。</summary>
    /// <param name="track">字幕轨道。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default);
}
