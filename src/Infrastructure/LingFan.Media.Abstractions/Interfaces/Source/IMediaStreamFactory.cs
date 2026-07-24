namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体流工厂接口。
/// </summary>
/// <remarks>Singleton 工厂，无状态。每次 Create() 返回新实例。</remarks>
public interface IMediaStreamFactory
{
    /// <summary>根据媒体源类型创建对应的 IMediaStream 实例。</summary>
    IMediaStream Create(IMediaSource source);
}
