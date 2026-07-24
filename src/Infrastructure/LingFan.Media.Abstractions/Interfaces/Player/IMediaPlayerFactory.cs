namespace LingFan.Media.Abstractions;

/// <summary>
/// 播放器工厂接口。
/// </summary>
/// <remarks>
/// 工厂自身为 Singleton（无状态）。每次 Create() 返回新实例（Session 级，Transient）。
/// </remarks>
public interface IMediaPlayerFactory
{
    /// <summary>创建新的 IMediaPlayer 实例。</summary>
    IMediaPlayer Create();
}
