namespace LingFan.Media.Outputs.OpenSLES;

/// <summary>
/// OpenSL ES 音频输出。V1 桩实现（Android）。
/// </summary>
/// <remarks>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// OpenSL ES 输出为 Phase 2 目标（Android）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class OpenSlesOutput : IAudioOutput
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "OpenSL ES 输出尚未实现。OpenSL ES 为 Phase 2 目标（Android）。");

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
        => throw new NotSupportedException("OpenSL ES 输出尚未实现。");

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        frame?.Dispose();
        throw new NotSupportedException("OpenSL ES 输出尚未实现。");
    }

    /// <inheritdoc/>
    public void Pause() => throw new NotSupportedException("OpenSL ES 输出尚未实现。");

    /// <inheritdoc/>
    public void Resume() => throw new NotSupportedException("OpenSL ES 输出尚未实现。");

    /// <inheritdoc/>
    public void Flush() => throw new NotSupportedException("OpenSL ES 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => throw new NotSupportedException("OpenSL ES 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan Latency => throw new NotSupportedException("OpenSL ES 输出尚未实现。");

    /// <inheritdoc/>
    public float Volume { get; set; }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
