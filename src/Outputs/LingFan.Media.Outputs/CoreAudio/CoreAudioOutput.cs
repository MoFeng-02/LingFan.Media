using System.Runtime.Versioning;
using LingFan.Media.Outputs.AppleAudioUnit;

namespace LingFan.Media.Outputs.CoreAudio;

/// <summary>
/// CoreAudio 音频输出（macOS）。P2 平台扩展（O2）。
/// </summary>
/// <remarks>
/// <para>职责：通过 AudioToolbox AudioUnit（kAudioUnitSubType_DefaultOutput）播放交错 S16 PCM，
/// 实现委托共用引擎 <see cref="AudioUnitEngine"/>（与 iOS RemoteIO 路径共用）。</para>
/// <para><b>异步策略</b>（与 WASAPI/AAudio 范本一致）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，平台校验后返回 <see cref="Task.CompletedTask"/>。
/// 无 I/O 可 await，<b>非伪异步</b>（不加 <c>async</c> 关键字、方法体无 <c>await</c>）。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类），创建 AudioUnit、设格式、注册渲染回调并启动。全部为同步原生调用。</item>
/// <item><see cref="Submit"/>：同步边界（native 分类），写入环形缓冲，满时阻塞背压（2 秒超时）。</item>
/// <item><see cref="Pause"/>/<see cref="Resume"/>/<see cref="Flush"/>：同步（sync 分类），AudioOutputUnitStop/Start/清缓冲。</item>
/// <item><see cref="GetPlaybackPosition"/>：同步，渲染回调累计消费帧数换算时间。</item>
/// <item><see cref="Dispose"/>：同步快速释放（sync 分类），Stop + Uninitialize + Dispose AudioUnit。</item>
/// <item><see cref="DisposeAsync"/>：接口契约，委托 <see cref="Dispose"/> + 返回 <see cref="ValueTask.CompletedTask"/>，非伪异步。</item>
/// </list>
/// <para><b>音量</b>：软件增益（S16 样本缩放），与 AAudio 一致。</para>
/// <para><b>所有权</b>：Submit 不接管帧所有权、不 Dispose 帧（规则），调用方负责 Return 到 FramePool 或 Dispose。</para>
/// <para><b>AOT 兼容</b>：sealed 类；纯 C API LibraryImport + <c>[UnmanagedCallersOnly]</c> 回调，零 COM、零反射、零 Obj-C 运行时。</para>
/// <para><b>平台边界</b>：仅 macOS 有效；非 macOS 调用抛 <see cref="PlatformNotSupportedException"/>。编译期跨平台可编译。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioOutput : IAudioOutput
{
    private readonly AudioUnitEngine _engine = new(AudioUnitEngine.SubTypeDefaultOutput, "CoreAudio");

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfNotMacOS();
        _engine.MarkReady();
        return Task.CompletedTask; // 契约方法：无真实 I/O await，非伪异步
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        ThrowIfNotMacOS();
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

    private static void ThrowIfNotMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("CoreAudio 输出仅支持 macOS。");
    }
}
