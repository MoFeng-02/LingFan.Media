using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// OpenAL 音频输出工厂（C 组 AUDIO-STUB 真实实现，跨平台回退输出）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。Create() 每次返回新的 <see cref="OpenALOutput"/> 实例（Session 级，设备句柄独立）。</para>
/// <para>Create() 为同步（config 分类），手动 new，无 I/O。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("android")]
public sealed class OpenALOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc/>
    public IAudioOutput Create() => new OpenALOutput();
}
