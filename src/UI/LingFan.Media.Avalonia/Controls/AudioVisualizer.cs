using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 音频可视化控件。显示频谱/波形/柱状频谱。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：全部 sync / native 分类——
/// Render 为 native（Avalonia void 签名硬限制），OnData/OnAudioFrame 为 sync（纯内存 Span 操作）。
/// 无任何 I/O 或 await，补 async 即伪异步（禁止）。</para>
/// <para><b>V2-09 U2/U3/U10</b>：对接真实音频管线数据（通过 <see cref="IMediaPlayer.AudioDataAvailable"/>
/// 事件），实现 FFT 频谱分析与真实波形/柱状渲染。V1 模拟数据作为无订阅时的回退。</para>
/// <para><b>L7 线程安全</b>：<see cref="_audioData"/> 由音频管线线程（OnAudioFrame）写入、
/// UI 线程（Render）读取，使用 <see cref="_audioDataLock"/> 保护。采用双缓冲语义——
/// 写入方总是构造新数组后整体替换引用，读取方在锁内捕获引用，锁外读取旧数组快照，无竞态。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；FFT 仅用 Span/ArrayPool；PCM→float 用 MemoryMarshal。</para>
/// </remarks>
public sealed class AudioVisualizer : Control
{
    private float[] _audioData = [];
    private readonly object _audioDataLock = new();
    private readonly Random _random = new();

    #region StyledProperties

    /// <summary>绑定播放器。</summary>
    public static readonly StyledProperty<IMediaPlayer?> PlayerProperty =
        AvaloniaProperty.Register<AudioVisualizer, IMediaPlayer?>(nameof(Player));

    /// <summary>可视化类型。</summary>
    public static readonly StyledProperty<VisualizerType> VisualizerTypeProperty =
        AvaloniaProperty.Register<AudioVisualizer, VisualizerType>(nameof(VisualizerType), defaultValue: VisualizerType.Bars);

    /// <summary>频谱柱数量。</summary>
    public static readonly StyledProperty<int> BarCountProperty =
        AvaloniaProperty.Register<AudioVisualizer, int>(nameof(BarCount), defaultValue: 32);

    /// <summary>更新频率（Hz）。</summary>
    public static readonly StyledProperty<int> UpdateRateProperty =
        AvaloniaProperty.Register<AudioVisualizer, int>(nameof(UpdateRate), defaultValue: 30);

    /// <summary>主颜色。</summary>
    public static readonly StyledProperty<Color> PrimaryColorProperty =
        AvaloniaProperty.Register<AudioVisualizer, Color>(nameof(PrimaryColor), defaultValue: Colors.White);

    /// <summary>次颜色。</summary>
    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<AudioVisualizer, Color>(nameof(SecondaryColor), defaultValue: Colors.Blue);

    #endregion

    #region StyledProperty Accessors

    /// <summary>绑定播放器。</summary>
    public IMediaPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>可视化类型。</summary>
    public VisualizerType VisualizerType
    {
        get => GetValue(VisualizerTypeProperty);
        set => SetValue(VisualizerTypeProperty, value);
    }

    /// <summary>频谱柱数量。</summary>
    public int BarCount
    {
        get => GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    /// <summary>更新频率（Hz）。</summary>
    public int UpdateRate
    {
        get => GetValue(UpdateRateProperty);
        set => SetValue(UpdateRateProperty, value);
    }

    /// <summary>主颜色。</summary>
    public Color PrimaryColor
    {
        get => GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    /// <summary>次颜色。</summary>
    public Color SecondaryColor
    {
        get => GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    #endregion

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // V2-09 U2：绑定播放器变化时订阅/取消订阅音频数据事件
        if (change.Property == PlayerProperty)
        {
            if (change.OldValue is IMediaPlayer oldPlayer)
                oldPlayer.AudioDataAvailable -= OnAudioFrame;
            if (change.NewValue is IMediaPlayer newPlayer)
                newPlayer.AudioDataAvailable += OnAudioFrame;
        }
    }

    /// <summary>
    /// 接收音频帧数据（V2-09 U2）。由 <see cref="IMediaPlayer.AudioDataAvailable"/> 在音频管线线程同步触发。
    /// <b>只读借用</b>传入的 <see cref="AudioFrame"/>：立即拷贝 PCM 字节并转换为单声道 float，
    /// 绝不持有帧引用或 Dispose（帧由管线池管理）。同步方法，无 I/O，无伪异步。
    /// </summary>
    private void OnAudioFrame(AudioFrame frame)
    {
        // 单声道下混后的浮点样本（长度 = 帧样本数）
        var samples = new float[Math.Max(1, frame.FrameCount)];
        DecodePcmToFloatMono(frame.Data.Span, frame.SampleFormat, frame.Channels, samples);

        // 双缓冲：构造新数组后整体替换引用（读取方在锁内捕获引用，锁外读取旧快照，无竞态）
        lock (_audioDataLock)
        {
            _audioData = samples;
        }

        // 本方法由音频管线线程（非 UI 线程）同步触发，InvalidateVisual 必须切回 UI 线程，
        // 否则抛出 InvalidOperationException（与 VideoView.OnSubtitleReceived / PresentFrame 既有模式一致）。
        Dispatcher.UIThread.Post(() => InvalidateVisual());
    }

    /// <summary>
    /// 接收音频数据（V1 兼容 / 测试入口）。同步方法——纯内存 Span 拷贝。
    /// </summary>
    /// <param name="data">音频采样数据。</param>
    public void OnData(Span<float> data)
    {
        float[] copy = data.Length == 0 ? [] : data.ToArray();
        lock (_audioDataLock)
        {
            _audioData = copy;
        }
        // 可能由非 UI 线程调用（如后台数据推送），统一切回 UI 线程刷新（与 OnAudioFrame 一致）。
        Dispatcher.UIThread.Post(() => InvalidateVisual());
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext drawingContext)
    {
        base.Render(drawingContext);

        var type = VisualizerType;
        if (type == VisualizerType.Bars)
            RenderBars(drawingContext);
        else if (type == VisualizerType.Waveform)
            RenderWaveform(drawingContext);
        else
            RenderSpectrum(drawingContext);
    }

    private void RenderBars(DrawingContext drawingContext)
    {
        var barCount = Math.Max(1, BarCount);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var barWidth = width / barCount;
        var gap = barWidth * 0.2;
        var actualBarWidth = barWidth - gap;

        var primaryBrush = new SolidColorBrush(PrimaryColor);
        var secondaryBrush = new SolidColorBrush(SecondaryColor);

        float[] data;
        lock (_audioDataLock) data = _audioData;

        for (var i = 0; i < barCount; i++)
        {
            // V2-09 U10：优先使用真实数据，无订阅时回退模拟
            var value = data.Length > 0 && i < data.Length
                ? Math.Abs(data[i])
                : (float)(_random.NextDouble() * 0.8 + 0.1);

            value = Math.Clamp(value, 0f, 1f);
            var barHeight = value * height;
            var x = i * barWidth + gap / 2;
            var y = height - barHeight;

            var rect = new Rect(x, y, actualBarWidth, barHeight);
            drawingContext.DrawRectangle(secondaryBrush, null, rect);

            if (barHeight > 4)
            {
                var topRect = new Rect(x, y, actualBarWidth, 3);
                drawingContext.DrawRectangle(primaryBrush, null, topRect);
            }
        }
    }

    private void RenderWaveform(DrawingContext drawingContext)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var midY = height / 2;
        var primaryPen = new Pen(new SolidColorBrush(PrimaryColor), 1.5);

        float[] data;
        lock (_audioDataLock) data = _audioData;

        // V2-09 U10：真实数据优先，无订阅时回退模拟
        var renderData = data.Length > 0 ? data : GenerateSimulatedWaveform(BarCount * 4);

        if (renderData.Length < 2)
            return;

        var stepX = width / (renderData.Length - 1);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, midY - renderData[0] * midY));
            for (var i = 1; i < renderData.Length; i++)
            {
                var x = i * stepX;
                var y = midY - renderData[i] * midY;
                ctx.LineTo(new Point(x, y));
            }
        }

        drawingContext.DrawGeometry(null, primaryPen, geometry);
    }

    private void RenderSpectrum(DrawingContext drawingContext)
    {
        float[] data;
        lock (_audioDataLock) data = _audioData;

        // 无真实数据时回退到 Bars（模拟数据），保持视觉连续性
        if (data.Length == 0)
        {
            RenderBars(drawingContext);
            return;
        }

        // V2-09 U3：FFT 频谱分析
        var n = FftProcessor.NextPow2(data.Length);
        if (n > 4096) n = 4096; // FFT 规模上限，防止极端长帧拖慢渲染

        var real = ArrayPool<float>.Shared.Rent(n);
        var imag = ArrayPool<float>.Shared.Rent(n);
        try
        {
            real.AsSpan(0, n).Clear();
            imag.AsSpan(0, n).Clear();

            var take = Math.Min(data.Length, n);
            data.AsSpan(0, take).CopyTo(real.AsSpan(0, take));

            FftProcessor.Forward(real.AsSpan(0, n), imag.AsSpan(0, n));

            // 幅度谱（去除 DC 分量，bin 区间 [1, n/2)）
            var bins = n >> 1;
            Span<float> mags = stackalloc float[bins];
            var maxMag = 1e-6f;
            for (var i = 1; i < bins; i++)
            {
                var m = MathF.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
                mags[i] = m;
                if (m > maxMag) maxMag = m;
            }

            var barCount = Math.Max(1, BarCount);
            var width = Bounds.Width;
            var height = Bounds.Height;
            var barWidth = width / barCount;
            var gap = barWidth * 0.2;
            var actualBarWidth = barWidth - gap;
            var brush = new SolidColorBrush(PrimaryColor);

            for (var b = 0; b < barCount; b++)
            {
                // 该柱对应频段的 bin 平均
                var lo = 1 + (b * (bins - 1)) / barCount;
                var hi = 1 + ((b + 1) * (bins - 1)) / barCount;
                var sum = 0f;
                var cnt = 0;
                for (var i = lo; i < hi && i < bins; i++)
                {
                    sum += mags[i];
                    cnt++;
                }
                var mag = cnt > 0 ? sum / cnt : 0f;
                var norm = Math.Clamp(mag / maxMag, 0f, 1f);
                var barHeight = norm * height;
                var x = b * barWidth + gap / 2;
                var y = height - barHeight;
                drawingContext.DrawRectangle(brush, null, new Rect(x, y, actualBarWidth, barHeight));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(real);
            ArrayPool<float>.Shared.Return(imag);
        }
    }

    /// <summary>
    /// 将交错 PCM 字节解码为单声道下混的 float 采样（控件内自实现，
    /// 因 Avalonia 层不引用 Audio 模块）。参考 Audio 模块的 PcmConversions 算法。
    /// </summary>
    private static void DecodePcmToFloatMono(ReadOnlySpan<byte> pcm, SampleFormat fmt, int channels, Span<float> dest)
    {
        if (channels <= 0) channels = 1;
        var avail = dest.Length;

        switch (fmt)
        {
            case SampleFormat.S16:
            {
                var s = MemoryMarshal.Cast<byte, short>(pcm);
                var frameCount = s.Length / channels;
                for (var i = 0; i < avail && i < frameCount; i++)
                {
                    long acc = 0;
                    for (var c = 0; c < channels; c++) acc += s[i * channels + c];
                    dest[i] = (acc / (float)channels) / 32768f;
                }
                break;
            }
            case SampleFormat.S32:
            {
                var s = MemoryMarshal.Cast<byte, int>(pcm);
                var frameCount = s.Length / channels;
                for (var i = 0; i < avail && i < frameCount; i++)
                {
                    long acc = 0;
                    for (var c = 0; c < channels; c++) acc += s[i * channels + c];
                    dest[i] = (acc / (float)channels) / 2147483648f;
                }
                break;
            }
            case SampleFormat.F32:
            {
                var s = MemoryMarshal.Cast<byte, float>(pcm);
                var frameCount = s.Length / channels;
                for (var i = 0; i < avail && i < frameCount; i++)
                {
                    var acc = 0f;
                    for (var c = 0; c < channels; c++) acc += s[i * channels + c];
                    dest[i] = acc / channels;
                }
                break;
            }
            default:
                // 未知格式：保持静音（dest 已清零）
                break;
        }
    }

    private float[] GenerateSimulatedWaveform(int count)
    {
        var data = new float[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = (float)(_random.NextDouble() * 2 - 1) * 0.5f;
        }
        return data;
    }
}
