namespace LingFan.Media.Outputs.Alsa;

/// <summary>
/// ALSA 音频输出。桩实现（Linux）。
/// </summary>
/// <remarks>
/// <para>桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// ALSA 输出为 Phase 2 目标（Linux）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class AlsaOutput : IAudioOutput
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "ALSA 输出尚未实现。ALSA 为 Phase 2 目标（Linux）。");

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
        => throw new NotSupportedException("ALSA 输出尚未实现。");

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        // 契约：Submit 不接管帧所有权（帧由调用方归还池或释放），桩实现同样不得 Dispose。
        throw new NotSupportedException("ALSA 输出尚未实现。");
    }

    /// <inheritdoc/>
    public void Pause() => throw new NotSupportedException("ALSA 输出尚未实现。");

    /// <inheritdoc/>
    public void Resume() => throw new NotSupportedException("ALSA 输出尚未实现。");

    /// <inheritdoc/>
    public void Flush() => throw new NotSupportedException("ALSA 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => throw new NotSupportedException("ALSA 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan Latency => throw new NotSupportedException("ALSA 输出尚未实现。");

    /// <inheritdoc/>
    public float Volume { get; set; }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
