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

    /// <summary>
    /// 端到端「调用 <see cref="Present"/> → 画面真正可见」的延迟（即 Present 到像素上屏的真实耗时）。
    /// <para>视频同步据此决定<b>提前多少调用 Present</b>，使画面恰好在音频到达该帧 PTS 时可见 ——
    /// 这是音画对齐的真正物理依据，绝非任意同步容差。</para>
    /// <para>有头 GPU 路径（D3D11/Vulkan 等，vsync 呈现）应返回显示器<b>刷新周期</b>
    /// （含 DWM/合成器缓冲，约 1~2 个刷新周期）；无头 / 测试桩 / 纯计算 sink 无真实上屏延迟，返回
    /// <see cref="TimeSpan.Zero"/>。</para>
    /// </summary>
    TimeSpan PresentationLatency { get; }
}
