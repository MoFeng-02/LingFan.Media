using System.Runtime.Versioning;
using LingFan.Media.Outputs.AppleAudioUnit;

namespace LingFan.Media.Outputs.AVAudioEngine;

/// <summary>
/// iOS 音频输出（RemoteIO AudioUnit）。P2 平台扩展（V2-18 / O3）。
/// </summary>
/// <remarks>
/// <para>职责：通过 AudioToolbox AudioUnit（kAudioUnitSubType_RemoteIO）播放交错 S16 PCM，
/// 实现委托共用引擎 <see cref="AudioUnitEngine"/>（与 macOS DefaultOutput 路径共用）。</para>
/// <para><b>技术选型</b>（用户 2026-07-28 拍板）：不走 Obj-C AVAudioEngine 封装（需 objc_msgSend 大量互操作且无 C API），
/// 改走 RemoteIO AudioUnit——iOS 上与 macOS 完全同构的 C API，仅组件子类型不同（'rioc' vs 'def '），
/// 类名保留 AvAudioEngineOutput 以维持既有 DI 注册面（AddAvAudioEngineOutput）不变。</para>
/// <para><b>异步策略</b>（与 WASAPI/AAudio 范本一致，遵守总记忆第十二章）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，平台校验后返回 <see cref="Task.CompletedTask"/>。
/// 无 I/O 可 await，<b>非伪异步</b>（不加 <c>async</c> 关键字、方法体无 <c>await</c>）。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），创建 RemoteIO AudioUnit、设格式、注册渲染回调并启动。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），写入环形缓冲，满时阻塞背压（2 秒超时）。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类）。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类）；<see cref="DisposeAsync"/>：接口契约，
/// 委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。</item>
/// </list>
/// <para><b>音量</b>：软件增益（S16 样本缩放），与 AAudio 一致。</para>
/// <para><b>所有权</b>：Submit 不接管帧所有权、不 Dispose 帧（V2 规则），调用方负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API LibraryImport + <c>[UnmanagedCallersOnly]</c> 回调，零 COM、零反射、零 Obj-C 运行时。</para>
/// <para><b>平台边界</b>：仅 iOS 有效；非 iOS 调用抛 <see cref="PlatformNotSupportedException"/>。编译期跨平台可编译。
/// AudioSession 类别配置（如后台播放、静音键行为）属宿主 App 职责，库内不做。</para>
/// </remarks>
[SupportedOSPlatform("ios")]
public sealed class AvAudioEngineOutput : IAudioOutput
{
    private readonly AudioUnitEngine _engine = new(AudioUnitEngine.SubTypeRemoteIO, "AVAudioEngine(RemoteIO)");

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfNotIOS();
        _engine.MarkReady();
        return Task.CompletedTask; // 契约方法：无真实 I/O await，非伪异步
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        ThrowIfNotIOS();
        _engine.Initialize(sampleRate, channels);
    }

    /// <inheritdoc/>
    public void Submit(AudioFrame frame) => _engine.Submit(frame);

    /// <inheritdoc/>
    public void Pause() => _engine.Pause();

    /// <inheritdoc/>
    public void Resume() => _engine.Resume();

    /// <inheritdoc/>
    public void Flush() => _engine.Flush();

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => _engine.GetPlaybackPosition();

    /// <inheritdoc/>
    public TimeSpan Latency => _engine.Latency;

    /// <inheritdoc/>
    public float Volume
    {
        get => _engine.Volume;
        set => _engine.Volume = value;
    }

    /// <inheritdoc/>
    public void Dispose() => _engine.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask; // 契约方法：无 I/O 可 await，非伪异步
    }

    private static void ThrowIfNotIOS()
    {
        if (!OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException("AVAudioEngine(RemoteIO) 输出仅支持 iOS。");
    }
}
