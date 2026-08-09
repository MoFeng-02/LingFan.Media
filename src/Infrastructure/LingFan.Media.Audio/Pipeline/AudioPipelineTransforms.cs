using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace LingFan.Media.Audio;

/// <summary>
/// 音频管线变换构造器。将 Audio 模块的富类型
/// （<see cref="VolumeControl"/> / <see cref="AudioMixer"/> / <see cref="MixerChannel"/>）
/// 转换为 Core <c>AudioPipeline</c> 可消费的中立 BCL 委托
/// <c>Func&lt;AudioFrame, AudioFrame&gt;</c>。
/// </summary>
/// <remarks>
/// <para>Core 不直接依赖 <c>LingFan.Media.Audio</c>，故音量/混音逻辑在此收敛为中立委托，
/// 由 DI/Extensions 层组合后注入 Core 管线，避免分层倒置。</para>
/// <para>所有权契约（与 Core.AudioPipeline 一致）：变换接收输入帧、Dispose 它并返回新帧；
/// 返回 null 表示丢弃帧（已 Dispose）。<see cref="AudioFrame.Data"/> 的 setter 为
/// Abstractions 程序集 internal，Audio 程序集内不可写，故音量缩放创建新帧并释放输入帧，
/// 与 <see cref="IAudioEffect.Process"/> 的所有权转移语义完全一致。</para>
/// <para>全部为纯内存同步操作，无 I/O、无 async（符合异步同步铁律：无真实 I/O 不补 async）。</para>
/// </remarks>
public static class AudioPipelineTransforms
{
    /// <summary>
    /// 构造音量变换。按 <see cref="VolumeControl.GetEffectiveVolume"/> 实时值
    /// 缩放 PCM 采样，创建新帧并释放输入帧（保持与 <see cref="IAudioEffect"/> 一致的所有权转移）。
    /// </summary>
    /// <param name="volumeControl">音量控制器（实时读取有效音量，含静音）。</param>
    /// <returns>音量变换委托。有效音量为 1.0 时透传输入帧（零分配）。</returns>
    public static Func<AudioFrame, AudioFrame> FromVolume(VolumeControl volumeControl)
    {
        ArgumentNullException.ThrowIfNull(volumeControl);
        return frame =>
        {
            var volume = volumeControl.GetEffectiveVolume();
            if (volume >= 1.0f)
                return frame; // 无需缩放，透传同一帧（零分配）

            // 捕获标量元数据（在 Dispose 前，避免任何跨 Disposed 访问的歧义）
            var sampleRate = frame.SampleRate;
            var channels = frame.Channels;
            var sampleFormat = frame.SampleFormat;
            var timestamp = frame.Timestamp;
            var duration = frame.Duration;
            var frameCount = frame.FrameCount;

            var scaledData = ScaleSamples(frame, volume);
            frame.Dispose(); // 释放输入帧（零拷贝帧释放原生引用计数）

            return new AudioFrame(
                scaledData,
                sampleRate,
                channels,
                sampleFormat,
                timestamp,
                duration,
                frameCount);
        };
    }

    /// <summary>
    /// 构造混音变换。将输入帧提交到指定 <see cref="MixerChannel"/>，
    /// 再经 <see cref="AudioMixer.Mix"/> 从所有激活通道混合出一路新帧。
    /// </summary>
    /// <param name="mixer">混音器（多路混合主控制器）。</param>
    /// <param name="channel">当前帧所属的混音通道（提交目标）。</param>
    /// <returns>混音变换委托。返回混合后的新（托管内存）帧。</returns>
    /// <remarks>
    /// 输入帧经 <see cref="MixerChannel.Submit"/> 拷贝到通道内部缓冲后，
    /// 由本闭包负责 Dispose（释放可能的零拷贝原生引用），再返回 <see cref="AudioMixer.Mix"/> 产生的新帧。
    /// </remarks>
    public static Func<AudioFrame, AudioFrame> FromMixer(AudioMixer mixer, MixerChannel channel)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(channel);
        return frame =>
        {
            var sampleCount = frame.FrameCount;
            channel.Submit(frame);   // 拷贝到通道缓冲（不 Dispose frame）
            frame.Dispose();        // 释放输入帧（零拷贝帧释放原生引用计数）
            return mixer.Mix(sampleCount); // 从通道缓冲混合出新帧
        };
    }

    private static byte[] ScaleSamples(AudioFrame frame, float volume)
    {
        // frame.Data.Span 在 Audio 程序集内为只读视图（Abstractions internal setter），
        // 故先拷贝到可变 byte[] 再原地缩放。
        var srcSpan = frame.Data.Span; // ReadOnlySpan<byte>
        var buffer = new byte[srcSpan.Length];
        srcSpan.CopyTo(buffer);
        var data = buffer.AsSpan(); // Span<byte>（可写）

        switch (frame.SampleFormat)
        {
            case SampleFormat.S16:
            {
                var count = data.Length / 2;
                for (int i = 0; i < count; i++)
                {
                    var s = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
                    var scaled = (short)Math.Clamp(s * volume, short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(data.Slice(i * 2, 2), scaled);
                }
                break;
            }
            case SampleFormat.S32:
            {
                var count = data.Length / 4;
                for (int i = 0; i < count; i++)
                {
                    var s = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4));
                    int scaled;
                    if (s >= int.MaxValue) scaled = int.MaxValue;
                    else if (s <= int.MinValue) scaled = int.MinValue;
                    else scaled = (int)(s * volume);
                    BinaryPrimitives.WriteInt32LittleEndian(data.Slice(i * 4, 4), scaled);
                }
                break;
            }
            case SampleFormat.F32:
            {
                var count = data.Length / 4;
                for (int i = 0; i < count; i++)
                {
                    var s = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
                    BinaryPrimitives.WriteSingleLittleEndian(data.Slice(i * 4, 4), Math.Clamp(s * volume, -1f, 1f));
                }
                break;
            }
            default:
                throw new NotSupportedException(
                    $"音量缩放不支持采样格式 {frame.SampleFormat}");
        }

        return buffer;
    }
}
