using System;

namespace LingFan.Media.Video.Processors;

/// <summary>
/// 帧率转换处理器。按固定 2:1 比例向目标帧率方向调整输出帧率。
/// </summary>
/// <remarks>
/// <para>升帧率：每隔一帧插入一个上一帧副本，输出约为输入的 1.5 倍；
/// 降帧率：每两帧丢弃一帧（返回 null，由管线丢弃已 Dispose 的帧），输出约为输入的一半。</para>
/// <para><see cref="TargetFrameRate"/> 仅用于判定升/降方向；与输入帧率匹配（±0.01fps）时透传。
/// 不做任意比例换算（如 24→60 需其他实现）。</para>
/// <para>仅处理打包软件帧；其余格式透传。同步热路径。</para>
/// </remarks>
public sealed class FrameRateConverter : IVideoProcessor
{
    /// <inheritdoc/>
    public string Name => "FrameRateConverter";

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>目标帧率（FPS，如 60）。仅用于判定升/降方向，实际转换比例固定 2:1（见类型 remarks）。</summary>
    public float TargetFrameRate { get; set; } = 60f;

    // 上一帧副本（用于插帧/对齐）；最多保留一帧
    private VideoFrame? _held;
    private long _counter;

    /// <inheritdoc/>
    public void Reset() => ReleaseHeld();

    /// <summary>初始化 <see cref="FrameRateConverter"/> 的新实例。</summary>
    public FrameRateConverter(float targetFrameRate = 60f)
    {
        TargetFrameRate = targetFrameRate;
    }

    /// <inheritdoc/>
    public VideoFrame? Process(VideoFrame frame)
    {
        if (!IsEnabled)
            return frame;
        if (TargetFrameRate <= 0f)
        {
            ReleaseHeld();
            return frame;
        }
        if (!FrameUtil.TryGetPackedSoftware(frame, out _, out _))
        {
            // 仅支持打包软件帧的精确插帧/丢帧；其余透传（不 Dispose）
            ReleaseHeld();
            return frame;
        }

        double srcFps = frame.Duration > TimeSpan.Zero
            ? 1.0 / frame.Duration.TotalSeconds
            : 30.0;
        double dstFps = TargetFrameRate;

        if (Math.Abs(srcFps - dstFps) < 0.01)
        {
            // 帧率匹配：同步 _held 并透传当前帧
            _held?.Dispose();
            _held = CopyFrame(frame);
            return frame;
        }

        _counter++;
        if (dstFps > srcFps)
        {
            // 升帧率：插帧（重复上一帧副本）
            if (_counter % 2 == 0 && _held != null)
            {
                return CopyFrame(_held); // 转移副本所有权；_held 仍由本类持有
            }
            // 真实帧回合：转移当前帧，保留其副本供下次插帧
            _held?.Dispose();
            _held = CopyFrame(frame);
            return frame;
        }
        else
        {
            // 降帧率：丢帧（每两帧丢一帧）
            if (_counter % 2 != 0)
            {
                _held?.Dispose();
                _held = null;
                frame.Dispose();
                return null; // 丢弃此帧（管线已感知 null）
            }
            _held?.Dispose();
            _held = CopyFrame(frame);
            return frame;
        }
    }

    private void ReleaseHeld()
    {
        _held?.Dispose();
        _held = null;
        _counter = 0;
    }

    private static VideoFrame CopyFrame(VideoFrame src)
    {
        if (src.Resource is not SoftwareFrameResource s)
            // 防御：TryGetPackedSoftware 已保证为打包软件帧，理论不可达；若违反则显式抛错，
            // 避免返回 src 导致 _held 与下游持有同一帧 → 双重 Dispose。
            throw new InvalidOperationException("CopyFrame 期望打包软件帧（FrameUtil.TryGetPackedSoftware 已保证），但收到非 SoftwareFrameResource 资源。");
        int bpp = FrameUtil.BytesPerPixel(s.Format);
        if (bpp == 0)
            // 防御：未知格式同样不可返回 src——否则 _held 与下游持有同一帧 → 双重 Dispose。
            throw new InvalidOperationException($"CopyFrame 收到未知像素格式 {s.Format}（BytesPerPixel=0），无法复制帧。");
        int len = s.Data.Length;
        var dst = new SoftwareFrameResource(s.Width, s.Height, s.Format, len);
        s.Data.Span.CopyTo(dst.Data.Span);
        return new VideoFrame(s.Width, s.Height, s.Format, dst, src.Timestamp, src.Duration, src.KeyFrame);
    }
}
