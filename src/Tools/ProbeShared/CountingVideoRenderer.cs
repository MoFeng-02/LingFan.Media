using LingFan.Media.Abstractions;

namespace ProbeShared;

/// <summary>
/// 计数装饰器工厂：包裹真实 <see cref="IVideoRendererFactory"/>，产出的渲染器统计 Present 调用并可按间隔落盘帧。
/// </summary>
/// <remarks>
/// 诊断用途：把渲染器收到的真实帧落 PNG（在 inner.Present 之前同步拷贝，不持有帧引用）。
/// 若落盘图干净而窗口画面异常，问题在渲染器上传/Present；若落盘图同样异常，问题在上游解码。
/// </remarks>
public sealed class CountingVideoRendererFactory : IVideoRendererFactory
{
    private readonly IVideoRendererFactory _inner;
    private readonly int _saveFrames;
    private readonly string _saveDir;
    private readonly int _maxDumpFrames;
    private readonly bool _printPixelStats;

    /// <summary>最近一次 Create 产出的计数渲染器（供判据段读 PresentCount）。</summary>
    public CountingVideoRenderer? Last { get; private set; }

    /// <summary>以真实工厂与落盘参数构造装饰器。</summary>
    /// <param name="inner">真实渲染器工厂。</param>
    /// <param name="saveFrames">每 N 帧落盘一张（0=不落盘）。</param>
    /// <param name="saveDir">落盘目录。</param>
    /// <param name="maxDumpFrames">落盘上限（0=不限）。</param>
    /// <param name="printPixelStats">落盘时是否打印像素统计（DPB 空帧/绿屏归因）。</param>
    public CountingVideoRendererFactory(IVideoRendererFactory inner, int saveFrames, string saveDir,
        int maxDumpFrames = 0, bool printPixelStats = false)
    {
        _inner = inner;
        _saveFrames = saveFrames;
        _saveDir = saveDir;
        _maxDumpFrames = maxDumpFrames;
        _printPixelStats = printPixelStats;
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        var wrapped = new CountingVideoRenderer(_inner.Create(), _saveFrames, _saveDir, _maxDumpFrames, _printPixelStats);
        Last = wrapped;
        return wrapped;
    }
}

/// <summary>
/// 计数装饰器渲染器：统计 Present 调用次数，按间隔把真实帧交 <see cref="FrameDumper"/> 落盘。
/// </summary>
public sealed class CountingVideoRenderer : IVideoRenderer
{
    private readonly IVideoRenderer _inner;
    private readonly int _saveFrames;
    private readonly string _saveDir;
    private readonly int _maxDumpFrames;
    private readonly bool _printPixelStats;

    /// <summary>Present 累计调用次数（Interlocked 递增）。</summary>
    public int PresentCount;

    /// <summary>以真实渲染器与落盘参数构造装饰器。</summary>
    /// <param name="inner">真实渲染器。</param>
    /// <param name="saveFrames">每 N 帧落盘一张（0=不落盘）。</param>
    /// <param name="saveDir">落盘目录。</param>
    /// <param name="maxDumpFrames">落盘上限（0=不限）。</param>
    /// <param name="printPixelStats">落盘时是否打印像素统计（DPB 空帧/绿屏归因）。</param>
    public CountingVideoRenderer(IVideoRenderer inner, int saveFrames, string saveDir,
        int maxDumpFrames = 0, bool printPixelStats = false)
    {
        _inner = inner;
        _saveFrames = saveFrames;
        _saveDir = saveDir;
        _maxDumpFrames = maxDumpFrames;
        _printPixelStats = printPixelStats;
    }

    /// <inheritdoc/>
    public void Attach(IRenderTarget target) => _inner.Attach(target);

    /// <inheritdoc/>
    public void Detach() => _inner.Detach();

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        int n = Interlocked.Increment(ref PresentCount);
        if (_saveFrames > 0 && n % _saveFrames == 0)
            FrameDumper.DumpFrame(frame, n, _saveDir, _maxDumpFrames, _printPixelStats);
        _inner.Present(frame);
    }

    /// <inheritdoc/>
    /// <remarks>透传真实渲染器的呈现延迟（部分探针判据依赖此值，不得吞为 Zero）。</remarks>
    public TimeSpan PresentationLatency => _inner.PresentationLatency;

    /// <inheritdoc/>
    public void Clear() => _inner.Clear();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>若真实渲染器实现指定接口则返回之（供探针读渲染器内部诊断，如 Vulkan IRendererProfiler）。</summary>
    /// <typeparam name="T">要探测的接口类型。</typeparam>
    /// <returns>真实渲染器作为 T 的引用；未实现返回 null。</returns>
    public T? InnerAs<T>() where T : class => _inner as T;
}