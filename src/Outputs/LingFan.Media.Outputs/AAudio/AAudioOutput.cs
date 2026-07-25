namespace LingFan.Media.Outputs.AAudio;

/// <summary>
/// AAudio 音频输出。V1 桩实现（Android API 27+）。
/// </summary>
/// <remarks>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// AAudio 输出为 Phase 2 目标（Android API 27+）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class AAudioOutput : IAudioOutput
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "AAudio 输出尚未实现。AAudio 为 Phase 2 目标（Android API 27+）。");

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
        => throw new NotSupportedException("AAudio 输出尚未实现。");

    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        frame?.Dispose();
        throw new NotSupportedException("AAudio 输出尚未实现。");
    }

    /// <inheritdoc/>
    public void Pause() => throw new NotSupportedException("AAudio 输出尚未实现。");

    /// <inheritdoc/>
    public void Resume() => throw new NotSupportedException("AAudio 输出尚未实现。");

    /// <inheritdoc/>
    public void Flush() => throw new NotSupportedException("AAudio 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => throw new NotSupportedException("AAudio 输出尚未实现。");

    /// <inheritdoc/>
    public TimeSpan Latency => throw new NotSupportedException("AAudio 输出尚未实现。");

    /// <inheritdoc/>
    public float Volume { get; set; }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
