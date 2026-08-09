namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频输出配置选项。
/// </summary>
/// <remarks>
/// <para>纯数据配置类（config 分类），无 I/O。</para>
/// <para>由 <see cref="WasapiExtensions.AddWasapiOutput"/> 的 configure 委托设置。</para>
/// </remarks>
public sealed class WasapiOptions
{
    /// <summary>是否使用独占模式。默认 false（共享模式）。</summary>
    /// <remarks>
    /// 共享模式与其他应用共享音频设备，兼容性好但延迟略高。
    /// 独占模式独占设备，延迟最低但其他应用无法播放声音。
    /// 推荐使用共享模式。
    /// </remarks>
    public bool ExclusiveMode { get; set; } = false;

    /// <summary>缓冲时长。默认 100ms。</summary>
    /// <remarks>缓冲越大越稳定但延迟越高。共享模式建议 100ms（兼顾稳定与 A/V 同步），独占模式可低至 10ms。</remarks>
    public TimeSpan BufferDuration { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>是否使用事件驱动模式。默认 true。</summary>
    /// <remarks>
    /// <para>新增。事件驱动模式下 WASAPI 通过内核事件通知缓冲区可写，替代 Thread.Sleep 轮询。</para>
    /// <para>优势：降低延迟（事件触发即唤醒 vs 轮询间隔）、减少 CPU 占用（无空转轮询）。</para>
    /// <para>事件驱动为 sync 原生边界（EventWaitHandle.WaitOne），与轮询同属 COM 背压机制，非伪异步。</para>
    /// <para>行为（轮询）可通过设为 false 恢复。</para>
    /// </remarks>
    public bool EventDrivenMode { get; set; } = true;

    /// <summary>期望的采样格式。null = 自动检测设备原生格式（推荐）。</summary>
    /// <remarks>
    /// <para>新增。控制 WASAPI 设备初始化时使用的采样格式。</para>
    /// <para>null（默认）：共享模式使用 GetMixFormat 获取设备原生格式；独占模式优先尝试 F32。</para>
    /// <para>指定格式：尝试以指定格式初始化设备，若不支持则回退到设备原生格式。</para>
    /// <para>当帧格式与设备格式匹配时，Submit 零转换直拷（O9 多格式直出）。</para>
    /// </remarks>
    public SampleFormat? PreferredSampleFormat { get; set; } = null;

    /// <summary>期望的采样率（Hz）。默认 44100。</summary>
    /// <remarks>实际采样率由 <see cref="WasapiOutput.Initialize"/> 设置。</remarks>
    public int SampleRate { get; set; } = 44100;

    /// <summary>期望的声道数。默认 2（立体声）。</summary>
    /// <remarks>实际声道数由 <see cref="WasapiOutput.Initialize"/> 设置。</remarks>
    public int Channels { get; set; } = 2;

    /// <summary>音频会话分类（IAudioClient2.SetClientProperties）首选值。默认 <see cref="AudioClientCategory.Movie"/>。
    /// 仅在 <see cref="EnableBackgroundCapableSession"/> 为 <c>true</c> 时生效（该开关默认关闭）。</summary>
    /// <remarks>
    /// <para>O10。需在 IAudioClient.Initialize 之前通过 IAudioClient2 设置；不支持 IAudioClient2 的旧系统自动跳过。</para>
    /// <para>设置失败（负 HRESULT）时自动降级到同族候选（BackgroundCapableMedia → Movie → Media），全部失败则静默跳过。</para>
    /// <para>历史注记（2026-08-02）：曾出现任意分类值都 <c>0xC0000005</c> 的现象，一度误判为「driver 全面损坏」。
    /// 真因是 <c>TrySetSessionCategory</c> 的 vtable 槽位算错一格（slotIndex 12 = 绝对槽 15 = <c>IsOffloadCapable</c>，
    /// 它比 SetClientProperties 多一个 <c>BOOL*</c> 出参，误调导致向未初始化寄存器指向的野地址写入）。
    /// 修正为 slotIndex 13（绝对槽 16）后，独立官方 COM 探针九个分类全部 <c>S_OK</c>。</para>
    /// </remarks>
    public AudioClientCategory SessionCategory { get; set; } = AudioClientCategory.Movie;

    /// <summary>是否启用 IAudioClient2.SetClientProperties 会话分类（默认 <c>false</c> = opt-in）。</summary>
    /// <remarks>
    /// <para>用途：把会话标记为媒体类，供音量混合器与电源策略参考；在确实会挂起后台会话的系统上可作为规避手段。</para>
    /// <para>失败保护：QI 拿不到 IAudioClient2 时静默跳过；某分类返回负 HRESULT 时降级试下一个候选；全部失败仅记 Warning，
    /// 不影响后续 <c>Initialize</c>。</para>
    /// <para>🔴 2026-08-02 定案（两条独立结论，勿混淆）：</para>
    /// <para>① <b>调用路径的 bug 已修</b>：此前任意分类都 <c>0xC0000005</c>，真因是 vtable 槽位算错一格
    /// （误调 <c>IsOffloadCapable</c>，它多一个 <c>BOOL*</c> 出参 ⇒ 向未初始化寄存器指向的野地址写入）。
    /// 修正为 slotIndex 13（绝对槽 16）后调用本身合法，独立官方 COM 探针九个分类全部 <c>S_OK</c>。</para>
    /// <para>② <b>但默认仍为 false</b>：启用它的原始动机是「防止 OS 挂起后台会话」，而该前提<b>并未被证实</b>
    /// （诊断探针的停滞判定在欠供给场景下恒不触发，属无效判定）；且用户本机实测默认启用后出现回归——
    /// <b>约 30s 静音后才出声</b>。在动机未证实而副作用确凿的情况下，按「不引入未经验证的默认行为」原则保持 opt-in。</para>
    /// <para>修改默认值后务必先做 <c>dotnet clean</c> + 全量重建再跑测试。</para>
    /// </remarks>
    public bool EnableBackgroundCapableSession { get; set; } = false;

    /// <summary>
    /// 预热后是否让 <see cref="IAudioEngine"/> 的 anchor 流保持 <c>Start</c> 状态。默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// <para>仅影响 <see cref="IAudioEngine.Warmup"/> 建立的<b>保活流</b>，与任何播放会话无关。</para>
    /// <para><c>true</c>（默认）：anchor 流 <c>Initialize</c> 后再 <c>Start</c>，成为一条活跃但<b>不写任何数据</b>
    /// （因而完全静音）的流，最大化"OS 音频引擎不回休眠"的概率——这正是让后续 Session 的
    /// <c>IAudioClient.Initialize</c> 走热路径、免掉 ~2.5s 冷启动的关键。</para>
    /// <para><c>false</c>：anchor 只 <c>Initialize</c> 不 <c>Start</c>。用于对照实验——若某些驱动上
    /// "仅 Initialize"已足够保活，可关掉以彻底避免一条常驻活跃流。</para>
    /// <para>注意：本项默认值改动会影响冷启动实测结论，调整前请先跑 <c>[WASAPI-ENGINE]</c> / <c>[WASAPI-OPEN]</c> 对照。</para>
    /// </remarks>
    public bool KeepEngineAnchorRunning { get; set; } = true;
}
