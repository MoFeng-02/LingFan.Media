namespace LingFan.Media.Abstractions;

/// <summary>
/// 解封装器工厂接口。
/// </summary>
/// <remarks>
/// Singleton 工厂，无状态。每次 Create() 返回新实例（每次播放新建，不共享）。
/// </remarks>
public interface IMediaDemuxerFactory
{
    /// <summary>根据媒体流创建对应的 IMediaDemuxer 实例。</summary>
    IMediaDemuxer Create(IMediaStream stream);
}
