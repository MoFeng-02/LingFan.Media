using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

// VideoView 的渲染器回退链职责段（部分类，按职责拆分，编译期等价）。
// 工厂解析/偏好前置/Skia 末位兜底/运行期不健康拉黑重建；Player 绑定与呈现在主文件。
public sealed partial class VideoView
{
    /// <summary>
    /// 按 DI 注册的 <see cref="IVideoRendererFactory"/> 集合，以「注册顺序 + 异常驱动回退」解析可用渲染器。
    /// 与 <c>BackendFallbackMediaPlayerFactory</c> 同构：逐个尝试 <c>Create()</c> + <c>Attach(this)</c>，
    /// 任一环节抛异常则安全降级到下一个工厂；<see cref="SkiaVideoRendererFactory"/> 接受
    /// <see cref="RenderHandleType.None"/> 渲染目标，作为末级兜底必然成功。
    /// </summary>
    /// <remarks>
    /// <para><b>Avalonia 控件约束</b>：VideoView 实现 <see cref="IRenderTarget"/> 且其
    /// <see cref="IRenderTarget.HandleType"/> 为 <see cref="RenderHandleType.None"/>（无子 HWND 宿主）。
    /// GPU 原生 SwapChain 渲染器（D3D11/Vulkan/Metal/OpenGL）的 <c>Attach</c> 要求
    /// <see cref="RenderHandleType.Pointer"/>（HWND），在控件内必抛 <see cref="NotSupportedException"/>
    /// → 自动回退到 Skia 软渲染。解码仍走 GPU（FFmpeg D3D11VA / MF DXVA），仅最终 blit 到位图，
    /// 与「GPU 友好且不固定 GPU」诉求一致。原生零拷贝上屏留作独立任务。</para>
    /// <para><b>无空域合成</b>：Skia 路径将帧写入 Avalonia 的 WriteableBitmap，由
    /// <see cref="Render(DrawingContext)"/> 经 <see cref="IAvaloniaRenderAware"/> 绘入合成树，
    /// 与 Avalonia 合成器共存，无黑屏/竞态。</para>
    /// <para><b>异步策略</b>：sync——纯内存 + 渲染器 Attach（GPU 失败抛异常，无 I/O await）。</para>
    /// <para><b>AOT 兼容</b>：无反射；工厂解析经 DI 抽象。</para>
    /// </remarks>
    private void EnsurePresenter()
    {
        if (_renderer != null)
            return;

        // 无 DI 容器：直接兜底内置 Skia——独立于注册顺序，保证视频必出。
        if (_services is null)
        {
            var skia = new SkiaVideoRenderer(_logger);
            skia.Attach(this);
            _renderer = skia;
            if (_renderer is SkiaVideoRenderer sk) sk.AspectRatioMode = AspectRatioMode;
            UpdateVideoSinkSubscription();
            return;
        }

        _logger ??= _services.GetService<ILogger<VideoView>>();

        // 解析全部已注册 IVideoRendererFactory（DI 注册顺序）。
        var factories = new List<IVideoRendererFactory>(_services.GetServices<IVideoRendererFactory>());

        // 1) Skia 永远置于末位（无论注册顺序如何，保证它是最终兜底）。
        factories.RemoveAll(f => f is SkiaVideoRendererFactory);

        // 2) 若指定 RendererType 偏好，将其对应工厂前置（按工厂命名约定匹配，避免为类型检查而 Create）。
        if (RendererType is { } preferred)
        {
            for (int i = 0; i < factories.Count; i++)
            {
                if (factories[i].GetType().Name.Contains(preferred.Name, StringComparison.Ordinal))
                {
                    var pref = factories[i];
                    factories.RemoveAt(i);
                    factories.Insert(0, pref);
                    break;
                }
            }
        }

        // 3) 末位追加 Skia 兜底工厂（Accept RenderHandleType.None，必然成功）。
        factories.Add(new SkiaVideoRendererFactory(_logger));

        // 异常驱动回退：依次尝试 Create() + Attach(this)。
        // GPU 渲染器在 Avalonia 控件内 Attach（需 Pointer/HWND）抛 NotSupportedException → 安全落入下一个；
        // 不 Dispose 失败的 GPU 单例渲染器——其共享 GPU 设备由工厂持有，部分 Attach 失败无资源泄漏。
        foreach (var factory in factories)
        {
            // 运行期已被判定不健康的工厂直接跳过（拉黑），避免无限重试。
            if (_failedFactories.Contains(factory.GetType()))
                continue;
            try
            {
                var renderer = factory.Create();
                renderer.Attach(this);
                _activeFactoryType = factory.GetType();
                if (renderer is IRendererHealth health)
                {
                    // Unhealthy 事件不带 sender，只能靠包装 lambda 捕获被订阅的渲染器实例来判断事件归属。
                    var source = renderer;
                    _healthHandler = () => OnRendererUnhealthy(source);
                    health.Unhealthy += _healthHandler;
                }
                _renderer = renderer;
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "IVideoRendererFactory（{Factory}）创建/附加失败，回退到下一个渲染器。",
                    factory.GetType().Name);
            }
        }

        // 极端兜底（理论不可达：末位必为 SkiaVideoRendererFactory 且 Attach(None) 必然成功）。
        if (_renderer is null)
        {
            var skia = new SkiaVideoRenderer(_logger);
            skia.Attach(this);
            _renderer = skia;
        }

        if (_renderer is SkiaVideoRenderer skiaFinal)
        {
            skiaFinal.AspectRatioMode = AspectRatioMode;
        }

        _logger?.LogInformation("VideoView 回退链解析完成，激活渲染器={Renderer}。", _renderer?.GetType().Name ?? "null");

        // 渲染器创建/重建后重新评估视频帧 sink 订阅（所有渲染器均经统一 FrameChannel 收帧）。
        UpdateVideoSinkSubscription();
    }

    private void OnRendererTypeChanged()
    {
        if (_renderer != null)
        {
            UnsubscribeRendererHealth(_renderer);
            _renderer.Detach();
            _renderer.Dispose();
            _renderer = null;
        }
        EnsurePresenter();
    }

    /// <summary>
    /// 渲染器运行期不健康（持续无法出画）回调：拉黑该工厂并重建回退链（落 Skia 末级兜底）。
    /// 由渲染器在管线线程触发，故切回 UI 线程处理，避免跨线程操作控件。
    /// </summary>
    /// <remarks>
    /// <para><b>安全网</b>：Composition 等 GPU 合成渲染器可能在 <see cref="IVideoRenderer.Attach"/>
    /// 成功、但运行期持续无法导入/呈现（跨设备纹理不被合成器接收）。此时 <see cref="IRendererHealth.Unhealthy"/>
    /// 触发本方法，把该工厂加入 <see cref="_failedFactories"/> 后重建回退链——保证永不静默空白，
    /// 任意渲染器失败都有 Skia 兜底，符合「后端/渲染器统一公平、不能让某个玩不了」原则。</para>
    /// </remarks>
    private void OnRendererUnhealthy(IVideoRenderer source)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 归属校验：事件只应来自订阅时捕获的那个渲染器。若它已不是当前渲染器（拆换过程中发出的迟到事件），
            // 必须丢弃——否则会把此刻在用的新渲染器连带拉黑，工厂被误判、回退链被无谓降级。
            if (_renderer is null || !ReferenceEquals(source, _renderer))
                return;
            if (_activeFactoryType is { } ft)
            {
                _logger?.LogWarning("渲染器（{Factory}）运行期不健康，回退到下一个渲染器。", ft.Name);
                _failedFactories.Add(ft);
            }
            var bad = _renderer;
            _renderer = null;
            UnsubscribeRendererHealth(bad);
            bad.Detach();
            bad.Dispose();
            EnsurePresenter();
        });
    }

    /// <summary>
    /// 退订渲染器的健康事件。渲染器被拆除前必须调用：<see cref="IRendererHealth.Unhealthy"/>
    /// 可能由渲染器内部持有的长生命周期对象触发，拆除后若不退订，重建时会叠加成重复订阅，
    /// 同一次不健康回调被处理多遍（重复拉黑工厂、重复重建回退链）。
    /// </summary>
    private void UnsubscribeRendererHealth(IVideoRenderer renderer)
    {
        if (renderer is not IRendererHealth health)
            return;

        // 用订阅时保存的同一委托实例退订：方法组转委托每次都会新建实例，直接 -= 方法组匹配不上会静默失效。
        if (_healthHandler is { } handler)
        {
            health.Unhealthy -= handler;
            _healthHandler = null;
        }
    }
}
