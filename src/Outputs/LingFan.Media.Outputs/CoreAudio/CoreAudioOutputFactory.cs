namespace LingFan.Media.Outputs.CoreAudio;

/// <summary>
/// CoreAudio 音频输出工厂。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。Create() 返回的 <see cref="CoreAudioOutput"/> 在使用时抛出 <see cref="NotSupportedException"/>。</para>
/// <para>Create() 为同步（config 分类），手动 new，无 I/O。</para>
/// </remarks>
public sealed class CoreAudioOutputFactory : IAudioOutputFactory
{
    /// <inheritdoc/>
    public IAudioOutput Create() => new CoreAudioOutput();
}
