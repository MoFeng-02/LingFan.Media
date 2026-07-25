namespace LingFan.Media.Outputs.CoreAudio;

/// <summary>
/// CoreAudio 音频输出。V1 桩实现（macOS）。
/// </summary>
/// <remarks>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// CoreAudio 输出为 Phase 2 目标（macOS）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class CoreAudioOutput : IAudioOutput
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "CoreAudio 输出尚未实现。CoreAudio 为 Phase 2 目标（macOS）。");

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
        => throw new NotSupportedException("CoreAudio 输出尚未实现。");

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        frame?.Dispose();
        throw new NotSupportedException("CoreAudio 输出尚未实现。");
    }

    /// <inheritdoc/>
    public void Pause() => throw new NotSupportedException("CoreAudio 输出尚未实现。");

    /// <inheritdoc/>
    public void Resume() => throw new NotSupportedException("CoreAudio 输出尚未实现。");

    /// <inheritdoc/>
    public void Flush() => throw new NotSupportedException("CoreAudio 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => throw new NotSupportedException("CoreAudio 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan Latency => throw new NotSupportedException("CoreAudio 输出尚未实现。");

    /// <inheritdoc/>
    public float Volume { get; set; }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
