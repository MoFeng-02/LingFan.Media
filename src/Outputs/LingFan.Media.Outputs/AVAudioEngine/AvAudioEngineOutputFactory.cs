using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.AVAudioEngine;

/// <summary>
/// iOS RemoteIO 音频输出工厂（真实实现，V2-18 / O3）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="AvAudioEngineOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>Create() 为同步（config 分类），手动 new，无 I/O。</para>
/// <para>平台边界：仅 iOS 有效；非 iOS 上创建实例本身无副作用，实际调用时抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[SupportedOSPlatform("ios")]
public sealed class AvAudioEngineOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc/>
    public IAudioOutput Create() => new AvAudioEngineOutput();
}
