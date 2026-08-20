using LingFan.Media.Abstractions;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头渲染器工厂。创建 <see cref="NoOpVideoRenderer"/> 空实例，无需 GPU 设备。
/// 经 <c>AddHeadlessRenderer()</c> 注册为 <see cref="IVideoRendererFactory"/>，替代具体渲染器工厂。
/// </summary>
public sealed class NoOpVideoRendererFactory : IVideoRendererFactory
{
    /// <inheritdoc />
    public IVideoRenderer Create() => new NoOpVideoRenderer();
}
