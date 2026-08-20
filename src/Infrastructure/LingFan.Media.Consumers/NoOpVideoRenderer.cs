using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头 / 服务端场景的空渲染器：不创建 GPU 设备、不初始化 SwapChain、<see cref="Present"/> 为 no-op。
/// 供无 <c>VideoView</c> / 无窗口场景替代具体渲染器，使 MediaPlayer 在无头下正常初始化与运行
/// （注册 <see cref="NoOpVideoRendererFactory"/> 而非 D3D11/Vulkan 渲染器工厂）。
/// </summary>
/// <remarks>
/// <para>无头 A 形态下，帧经 <see cref="IMediaPlayer.VideoFrameAvailable"/> 流向计算 sink，
/// 永不进入本渲染器的 <see cref="Present"/>；即便进入（无订阅方时），<see cref="Present"/> 也为安全空操作。</para>
/// <para>实现 <see cref="IVideoRenderer"/>（: <see cref="IMediaComponent"/> = <see cref="IDisposable"/> + <see cref="IAsyncDisposable"/>），生命周期闭环无原生资源。</para>
/// <para>AOT 兼容：<see langword="sealed"/>、无反射、无 P/Invoke。</para>
/// </remarks>
public sealed class NoOpVideoRenderer : IVideoRenderer
{
    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Attach(IRenderTarget target) { }

    /// <inheritdoc />
    public void Detach() { }

    /// <inheritdoc />
    public void Present(VideoFrame frame) { }

    /// <inheritdoc />
    public void Clear() { }

    /// <inheritdoc />
    public TimeSpan PresentationLatency => TimeSpan.Zero;

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
