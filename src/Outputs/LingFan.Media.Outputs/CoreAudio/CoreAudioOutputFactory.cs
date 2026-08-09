using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.CoreAudio;

/// <summary>
/// CoreAudio 音频输出工厂（macOS 真实实现，O2）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="CoreAudioOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>Create() 为同步（config 分类），手动 new，无 I/O。</para>
/// <para>平台边界：仅 macOS 有效；非 macOS 上创建实例本身无副作用，实际调用时抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc/>
    public IAudioOutput Create() => new CoreAudioOutput();
}
