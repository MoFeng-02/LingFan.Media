using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 音频可视化控件。显示频谱/波形/柱状频谱。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：全部 sync / native 分类——
/// Render 为 native（Avalonia void 签名硬限制），OnData 为 sync。</para>
/// <para><b>V1 简化</b>：先实现 Bars 模式，用模拟数据。V2 对接实际音频管线数据。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。</para>
/// </remarks>
public sealed class AudioVisualizer : Control
{
    private float[] _audioData = [];
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

    /// <summary>
    /// 接收音频数据。同步方法——纯内存 Span 操作。
    /// V2 对接实际音频管线。
    /// </summary>
    /// <param name="data">音频采样数据。</param>
    public void OnData(Span<float> data)
    {
        if (_audioData.Length != data.Length)
            _audioData = new float[data.Length];

        data.CopyTo(_audioData);
        InvalidateVisual();
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

        for (var i = 0; i < barCount; i++)
        {
            // V1: 模拟数据
            var value = _audioData.Length > 0 && i < _audioData.Length
                ? Math.Abs(_audioData[i])
                : (float)(_random.NextDouble() * 0.8 + 0.1);

            value = Math.Clamp(value, 0f, 1f);
            var barHeight = value * height;
            var x = i * barWidth + gap / 2;
            var y = height - barHeight;

            var rect = new Rect(x, y, actualBarWidth, barHeight);

            // 渐变效果：底部用次色，顶部用主色
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

        var data = _audioData.Length > 0
            ? _audioData
            : GenerateSimulatedWaveform(BarCount * 4);

        if (data.Length < 2)
            return;

        var stepX = width / (data.Length - 1);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, midY - data[0] * midY));
            for (var i = 1; i < data.Length; i++)
            {
                var x = i * stepX;
                var y = midY - data[i] * midY;
                ctx.LineTo(new Point(x, y));
            }
        }

        drawingContext.DrawGeometry(null, primaryPen, geometry);
    }

    private void RenderSpectrum(DrawingContext drawingContext)
    {
        // V1: Spectrum 模式与 Bars 相同（V2 实现 FFT）
        RenderBars(drawingContext);
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
