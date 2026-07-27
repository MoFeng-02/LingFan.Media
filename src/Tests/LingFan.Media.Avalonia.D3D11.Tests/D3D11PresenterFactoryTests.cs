using LingFan.Media.Abstractions;
using LingFan.Media.Avalonia;
using LingFan.Media.Avalonia.D3D11;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LingFan.Media.Avalonia.D3D11.Tests;

/// <summary>
/// D3D11PresenterFactory 单元测试。验证工厂类型匹配机制（VideoView 按 RendererType 解析 Presenter 的关键）。
/// 不触真实 D3D11 设备——D3D11GpuPresenter 的设备创建延迟到 Initialize→Attach，Create() 仅 new 对象。
/// </summary>
public class D3D11PresenterFactoryTests
{
    [Fact]
    public void PresenterType_ReturnsD3D11GpuPresenter()
    {
        var factory = new D3D11PresenterFactory(new StubVideoRendererFactory(), NullLoggerFactory.Instance);
        Assert.Equal(typeof(D3D11GpuPresenter), factory.PresenterType);
    }

    [Fact]
    public void Create_ReturnsD3D11GpuPresenterInstance()
    {
        var factory = new D3D11PresenterFactory(new StubVideoRendererFactory(), NullLoggerFactory.Instance);
        var presenter = factory.Create();
        Assert.IsType<D3D11GpuPresenter>(presenter);
    }

    [Fact]
    public void PresenterType_DiffersFromSkiaFactory()
    {
        // VideoView 的工厂匹配机制依赖 PresenterType 的 Type 比较区分后端；
        // D3D11 与 Skia 工厂的 PresenterType 必须不同，否则无法区分。
        var d3d11 = new D3D11PresenterFactory(new StubVideoRendererFactory(), NullLoggerFactory.Instance);
        var skia = new SkiaPresenterFactory();
        Assert.NotEqual(d3d11.PresenterType, skia.PresenterType);
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
