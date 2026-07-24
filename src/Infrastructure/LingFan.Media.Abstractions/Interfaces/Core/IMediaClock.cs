namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体时钟接口。音视频同步的核心组件。
/// </summary>
/// <remarks>
/// <para>纯内存操作，无 I/O 等待，不需要 CancellationToken。</para>
/// <para>Clock 不能注册 Singleton——多播放器会抢同一时钟。</para>
/// <para>线程安全：所有属性和方法需线程安全（lock 或 Interlocked），</para>
/// <para>因为视频管线和音频管线都会访问时钟。</para>
/// </remarks>
public interface IMediaClock
{
    /// <summary>当前时钟位置。</summary>
    TimeSpan Position { get; }

    /// <summary>时钟速度（1.0=正常）。</summary>
    float Speed { get; set; }

    /// <summary>是否运行。</summary>
    bool IsRunning { get; }

    /// <summary>同步源。</summary>
    ClockSyncSource SyncSource { get; set; }

    /// <summary>同步阈值（默认 50ms）。</summary>
    TimeSpan SyncThreshold { get; set; }

    /// <summary>丢帧阈值（默认 200ms）。</summary>
    TimeSpan DropThreshold { get; set; }

    /// <summary>启动时钟，记录起始时间戳。</summary>
    void Start();

    /// <summary>暂停时钟，冻结当前位置。</summary>
    void Pause();

    /// <summary>重置到 Zero，停止运行。</summary>
    void Reset();

    /// <summary>跳转到指定位置。</summary>
    void SeekTo(TimeSpan position);

    /// <summary>同步到主时钟位置。</summary>
    void SyncTo(TimeSpan masterPosition);
}
