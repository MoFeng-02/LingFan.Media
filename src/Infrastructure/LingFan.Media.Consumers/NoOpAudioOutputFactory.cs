using LingFan.Media.Abstractions;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头空音频输出工厂（<see cref="NoOpAudioOutput"/> 的 Singleton 工厂）。
/// 与 <see cref="NoOpVideoRendererFactory"/> 对称，供 <c>AddSilentAudioOutput()</c> 注册。
/// </summary>
public sealed class NoOpAudioOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc />
    public IAudioOutput Create() => new NoOpAudioOutput();
}
