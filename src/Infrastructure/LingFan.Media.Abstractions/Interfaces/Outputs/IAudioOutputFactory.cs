namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频输出工厂接口。
/// </summary>
/// <remarks>Singleton 工厂，无状态。每次 Create() 返回新实例（设备句柄独立）。</remarks>
public interface IAudioOutputFactory
{
    /// <summary>创建新的 IAudioOutput 实例。</summary>
    IAudioOutput Create();
}
