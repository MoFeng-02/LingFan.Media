using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegAudioDecoder 的帧构建职责段（部分类，按职责拆分，编译期等价）。
// AVFrame→AudioFrame（交错/平面逐样本交错、引用计数零拷贝）与 SwrContext 重采样；
// 解码编排在主文件。
internal sealed partial class FFmpegAudioDecoder : IAudioDecoder, IFramePoolAware<AudioFrame>
{
    /// <summary>从 AVFrame 创建 AudioFrame。</summary>
    /// <remarks>
    /// <para>若 _framePool 可用，从池中 Rent 帧壳并调用 Reset 填充数据，复用 AudioFrame 实例减少 GC。</para>
    /// <para>交错格式（S16/FLT 等）data[0] 即完整 PCM——
    /// av_frame_clone 引用计数共享原生 buffer，AudioFrame 直接映射 data[0]（长度语义与拷贝路径
    /// 一致：linesize[0]，消费方 WasapiOutput 已按 expectedDataSize 截取）。
    /// 平面格式（FLTP 等）需拼接多平面为交错内存布局，保持既有拷贝路径。</para>
    /// </remarks>
    private unsafe AudioFrame CreateAudioFrameFromAVFrame(AVFrame* avFrame, AVCodecContext* ctx)
    {
        TimeSpan timestamp = avFrame->pts != FF.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;

        int frameCount = avFrame->nb_samples;
        int channels = avFrame->ch_layout.nb_channels;
        int sampleRate = avFrame->sample_rate;
        AVSampleFormat sampleFmt = (AVSampleFormat)avFrame->format;

        // AAC 等解码器在 avcodec_open2 后 ctx->sample_rate 可能仍为 0（采样率仅在首帧解出后可知）。
        // 用首帧 avFrame->sample_rate / 声道数回填 OutputSampleRate/OutputChannels，使解码器向外导出正确采样率
        // （供 WASAPI 等设备打开、NoOp 实时背压等；Initialize 时刻读到的 0 是 ffmpeg 延迟填充所致）。
        if (OutputSampleRate <= 0 && sampleRate > 0) OutputSampleRate = sampleRate;
        if (OutputChannels <= 0 && channels > 0) OutputChannels = channels;

        SampleFormat outFormat = MapSampleFormatFromFFmpeg(sampleFmt);

        bool isPlanar = FF.av_sample_fmt_is_planar(sampleFmt) != 0;
        int bytesPerSample = FF.av_get_bytes_per_sample(sampleFmt);
        if (bytesPerSample <= 0)
            throw new InvalidOperationException($"无效的音频采样格式: {sampleFmt}");
        int planeSize = frameCount * bytesPerSample; // 每个平面的数据大小

        ReadOnlyMemory<byte> data;
        IDisposable? dataOwner = null;

        if (!isPlanar)
        {
            // 交错格式（如 S16, FLT）：data[0] 包含所有数据
            int dataSize = avFrame->linesize[0];
            if (dataSize < 0)
                throw new InvalidOperationException($"无效的音频行大小: {dataSize}");

            // 引用计数共享原生 buffer；克隆失败（非引用计数帧/OOM）回退拷贝
            AVFrame* clone = FF.av_frame_clone(avFrame);
            if (clone != null && clone->data[0] != IntPtr.Zero)
            {
                var owner = new SafeAVFrameHandle((IntPtr)clone);
                data = new NativeBufferMemoryManager((IntPtr)clone->data[0], dataSize).Memory;
                dataOwner = owner;
            }
            else
            {
                if (clone != null)
                {
                    AVFrame* p = clone;
                    FF.av_frame_free(&p);
                }
                var buffer = new byte[dataSize];
                Marshal.Copy((IntPtr)avFrame->data[0], buffer, 0, dataSize);
                data = buffer;
            }
        }
        else
        {
            // 平面格式（如 FLTP）：必须**逐样本交错**写入，绝不能按平面块拼接。
            // 下游（WASAPI/无头音频）一律按交错语义解释 SampleFormat；若此处留平面块布局
            // [L0..Ln][R0..Rn]，会被当成 [L0 R0 L1 R1…] 解释 → 每样本前半左声道、后半右声道，
            // 周期性畸变 → 典型「电音」。原始注释声称"拼接多平面为交错内存布局"但实现从未交错。
            int dataSize = planeSize * channels;
            if (dataSize < 0)
                throw new InvalidOperationException($"无效的音频数据大小: {dataSize}（frameCount={frameCount}, channels={channels}）");
            var buffer = new byte[dataSize];
            fixed (byte* dstBase = buffer)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (avFrame->extended_data[ch] == null) continue;
                    byte* srcPlane = avFrame->extended_data[ch];
                    for (int i = 0; i < frameCount; i++)
                    {
                        byte* src = srcPlane + (long)i * bytesPerSample;
                        byte* dst = dstBase + (long)(i * channels + ch) * bytesPerSample;
                        for (int b = 0; b < bytesPerSample; b++)
                            dst[b] = src[b];
                    }
                }
            }
            data = buffer;
        }

        TimeSpan duration = sampleRate > 0
            ? TimeSpan.FromTicks((long)frameCount * TimeSpan.TicksPerSecond / sampleRate)
            : TimeSpan.Zero;

        // 从池中 Rent 帧壳并 Reset 填充数据（Reset 会释放旧的零拷贝所有者）
        var frame = _framePool?.Rent() ?? new AudioFrame();
        frame.Reset(data, sampleRate, channels, outFormat, timestamp, duration, frameCount, dataOwner);
        return frame;
    }

    // 重采样

    /// <summary>将已解码的源格式 AVFrame 重采样为目标格式（如需要）。</summary>
    /// <remarks>纯原生同步操作（swr_convert_frame），不引入异步。</remarks>
    private unsafe AVFrame* ConvertWithSwr(AVFrame* inFrame)
    {
        AVFrame* outFrame = FF.av_frame_alloc();
        if (outFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败（重采样输出）");
        try
        {
            outFrame->format = (int)_targetSampleFormat;
            outFrame->sample_rate = _targetSampleRate;
            FF.av_channel_layout_default(&outFrame->ch_layout, _targetChannels);

            SwrContext* swr = (SwrContext*)_swrContext!.DangerousGetHandle();
            int outSamples = FF.swr_get_out_samples(swr, inFrame->nb_samples);
            outFrame->nb_samples = outSamples > 0 ? outSamples : inFrame->nb_samples;

            int ret = FF.av_frame_get_buffer(outFrame, 0);
            if (ret < 0)
                throw new InvalidOperationException($"av_frame_get_buffer 失败: {GetErrorString(ret)} (code={ret})");

            ret = FF.swr_convert_frame(swr, outFrame, inFrame);
            if (ret < 0)
                throw new InvalidOperationException($"swr_convert_frame 失败: {GetErrorString(ret)} (code={ret})");
            return outFrame;
        }
        catch
        {
            AVFrame* p = outFrame;
            FF.av_frame_free(&p);
            throw;
        }
    }

    /// <summary>将 AVFrame 封装为 AudioFrame，必要时先经 SwrContext 重采样。</summary>
    /// <remarks>重采样为纯原生同步操作，不引入异步；<see cref="DecodeAsync"/> 仍返回 <see cref="ValueTask"/>。</remarks>
    private unsafe AudioFrame? ReceiveToAudioFrame(AVFrame* avFrame, AVCodecContext* ctx)
    {
        if (_swrContext == null)
            return CreateAudioFrameFromAVFrame(avFrame, ctx);

        AVFrame* outFrame = ConvertWithSwr(avFrame);
        try
        {
            // 重采样不改变时间戳：源帧无 PTS 时沿用源，确保音频时间轴连续
            if (outFrame->pts == FF.AV_NOPTS_VALUE)
                outFrame->pts = avFrame->pts;
            return CreateAudioFrameFromAVFrame(outFrame, ctx);
        }
        finally
        {
            AVFrame* p = outFrame;
            FF.av_frame_free(&p);
        }
    }
}
