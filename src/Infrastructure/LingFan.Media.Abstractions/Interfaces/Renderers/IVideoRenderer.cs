namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频渲染器接口。
/// </summary>
/// <remarks>
/// <para>线程模型：</para>
/// <list type="bullet">
/// <item>Attach / Detach 在 UI 线程调用</item>
/// <item>Present / Clear 在渲染线程调用</item>
/// </list>
/// <para>IFrameResource 非线程安全，需在单线程内使用。</para>
/// </remarks>
public interface IVideoRenderer : IMediaComponent
{
    /// <summary>绑定渲染目标。UI 线程调用。</summary>
    void Attach(IRenderTarget target);

    /// <summary>解绑渲染目标。UI 线程调用。</summary>
    void Detach();

    /// <summary>
    /// 呈现一帧。渲染线程调用。
    /// Present 为同步消费——Renderer 在返回前完成 GPU 资源上传/拷贝，
    /// 调用方即可安全释放帧；若某 Renderer 需异步保留帧，应由该 Renderer 自行接管所有权。
    /// </summary>
    void Present(VideoFrame frame);

    /// <summary>清除当前画面。渲染线程调用。</summary>
    void Clear();
}
