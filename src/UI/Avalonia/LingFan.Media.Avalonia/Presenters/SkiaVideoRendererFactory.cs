using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Skia 视频渲染器工厂（Avalonia UI 层）。注册为 <see cref="IVideoRendererFactory"/>，
/// 作为 <see cref="VideoView"/> 回退链路的末级兜底——所有 GPU 渲染器在 Avalonia 控件内无法合成时回退到此。
/// </summary>
/// <remarks>
/// <para><b>回退位置</b>：由 <see cref="AvaloniaExtensions.AddSkiaPresenter"/> 注册为最后一个
/// <see cref="IVideoRendererFactory"/>（注册顺序末位），故在 DI 回退循环中最后被尝试且必然成功
/// （接受 <see cref="RenderHandleType.None"/> 渲染目标）。</para>
/// <para><b>异步策略</b>：config 分类——纯工厂方法，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class SkiaVideoRendererFactory : IVideoRendererFactory
{
    private readonly ILogger? _logger;

    /// <summary>构造 Skia 软渲染渲染器工厂；可选注入日志器透传给渲染器。</summary>
    public SkiaVideoRendererFactory(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public IVideoRenderer Create() => new SkiaVideoRenderer(_logger);
}
