namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频可视化类型。
/// </summary>
public enum VisualizerType : int
{
    /// <summary>频谱（FFT 频域分析）。</summary>
    Spectrum,
    /// <summary>波形（时域波形）。</summary>
    Waveform,
    /// <summary>经典柱状频谱。</summary>
    Bars
}
