using LingFan.Media.Abstractions;
using LingFan.Media.Presenters;
using LingFan.Media.Presenters.D3D11;
using LingFan.Media.Renderers.D3D11;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LingFan.Media.GpuPresenter.Tests;

/// <summary>
/// D3D11GpuPresenterFactory 单元测试。验证工厂类型匹配机制（VideoView 按 RendererType 解析 Presenter 的关键）。
/// 不触真实 D3D11 设备——D3D11GpuPresenter 的设备创建延迟到 Initialize→Attach，Create() 仅 new 对象。
/// </summary>
public class D3D11GpuPresenterFactoryTests
{
    [Fact]
    public void PresenterType_ReturnsD3D11GpuPresenter()
    {
        var factory = new D3D11GpuPresenterFactory(new StubVideoRendererFactory(), NullLoggerFactory.Instance);
        Assert.Equal(typeof(D3D11GpuPresenter), factory.PresenterType);
    }

    [Fact]
    public void Create_ReturnsD3D11GpuPresenterInstance()
    {
        var factory = new D3D11GpuPresenterFactory(new StubVideoRendererFactory(), NullLoggerFactory.Instance);
        var presenter = factory.Create();
        Assert.IsType<D3D11GpuPresenter>(presenter);
    }

    [Fact]
    public void PresenterType_DiffersFromOtherBackends()
    {
        // VideoView 的工厂匹配机制依赖 PresenterType 的 Type 比较区分后端；
        // D3D11 与 Vulkan 工厂的 PresenterType 必须不同，否则无法区分。
        var d3d11 = new D3D11GpuPresenterFactory(new StubVideoRendererFactory(), NullLoggerFactory.Instance);
        Assert.NotEqual(typeof(LingFan.Media.Presenters.Vulkan.VulkanGpuPresenter), d3d11.PresenterType);
    }
}

/// <summary>测试桩：不创建真实 GPU 设备，仅满足 IVideoRenderer 契约。</summary>
internal sealed class StubVideoRendererFactory : IVideoRendererFactory
{
    public IVideoRenderer Create() => new StubVideoRenderer();
}

internal sealed class StubVideoRenderer : IVideoRenderer
{
    public void Attach(IRenderTarget target) { }
    public void Detach() { }
    public void Present(VideoFrame frame) => frame.Dispose();
    public void Clear() { }
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
}
