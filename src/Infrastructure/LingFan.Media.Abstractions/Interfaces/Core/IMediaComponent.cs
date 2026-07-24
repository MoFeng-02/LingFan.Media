namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体组件生命周期基接口。
/// </summary>
/// <remarks>
/// <para>IDisposable + IAsyncDisposable 双继承：所有组件统一拥有
/// void Dispose()（同步快速路径）和 ValueTask DisposeAsync()（异步完整路径，推荐）。</para>
/// <para>继承此接口的接口：IVideoDecoder、IAudioDecoder、ISubtitleDecoder、
/// IVideoRenderer、IAudioOutput、IMediaDemuxer。</para>
/// <para>不暴露 bool IsInitialized——状态属于实现。</para>
/// </remarks>
public interface IMediaComponent : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 异步初始化组件。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task InitializeAsync(CancellationToken ct = default);

    // Dispose() 来自 IDisposable —— 同步快速释放路径
    // DisposeAsync() 来自 IAsyncDisposable —— 异步完整释放路径（推荐）
}
