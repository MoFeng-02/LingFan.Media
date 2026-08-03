namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频引擎（Infrastructure 级长期原生资源）。表示"进程内唯一、跨播放会话共享"的底层音频子系统连接，
/// 与 <see cref="IAudioOutput"/>（Session 级、每次 <c>OpenAsync</c> 新建）严格分层。
/// </summary>
/// <remarks>
/// <para><b>为什么需要这一层</b>：某些平台的音频子系统在"进程内第一个音频流建立"时会付出一次性冷启动开销
/// （典型如 Windows WASAPI：首个 <c>IAudioClient.Initialize</c> 触发 audiodg.exe 拉起，实测约 2.5s）。
/// 该开销的作用域是<b>操作系统音频引擎</b>而非某个播放会话，因此它必须由一个长生命周期对象承担一次，
/// 而不是让每个 Session 反复支付。</para>
/// <para><b>DI 生命周期</b>：<b>Singleton</b>。与 GPU 设备的分层范式一致——
/// <c>ID3D11Device</c>/<c>VkDevice</c> 是 Singleton 共享，而 <c>SwapChain</c>/<c>RenderTarget</c> 是 Session 级；
/// 对应到音频：<b>引擎/设备端点句柄是 Singleton，<see cref="IAudioOutput"/> 是 Transient</b>。
/// 绝不可把 Session 状态对象（持有播放线程、缓冲区、音量）升为 Singleton。</para>
/// <para><b>与 Session 的耦合度</b>：零。<see cref="IAudioOutput"/> 不引用本接口，也不依赖其内部原生对象；
/// 二者仅通过"操作系统音频引擎已处于热态"这一<b>进程级副作用</b>间接协作。这样多轨播放（N 个并发 Session）
/// 天然由 DI 各自 new/dispose，互不污染，也不需要任何手工引用计数。</para>
/// <para><b>调用时机由 host 决定</b>：库只暴露能力，不代管"何时预热"。宿主应在启动画面/加载界面期调用
/// <see cref="WarmupAsync"/> 吸收冷启动成本；不调用则维持原行为（成本落在首次播放）。</para>
/// <para><b>失败语义</b>：预热失败一律视为"未预热"，绝不能中断宿主启动，也不影响后续播放
/// （正式播放路径仍会完整初始化音频）。</para>
/// </remarks>
public interface IAudioEngine : IDisposable, IAsyncDisposable
{
    /// <summary>音频引擎是否已处于热态（预热成功且长期资源仍存活）。</summary>
    bool IsWarm { get; }

    /// <summary>
    /// 同步预热音频引擎。幂等：重复调用直接返回。
    /// </summary>
    /// <remarks>会阻塞调用线程直到冷启动完成（可能数秒），不要在 UI 线程调用；
    /// UI 宿主请用 <see cref="WarmupAsync"/>。</remarks>
    void Warmup();

    /// <summary>
    /// 异步预热音频引擎。幂等：重复调用直接返回（已在预热中则复用同一次进行中的操作）。
    /// </summary>
    /// <param name="ct">取消令牌。取消只中止<b>等待</b>，已投递的原生初始化不会被回滚。</param>
    Task WarmupAsync(CancellationToken ct = default);
}
