using System.Linq;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.MediaFoundation.Decoders;
using LingFan.Media.Backends.MediaFoundation.Demuxer;
using LingFan.Media.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// MediaFoundation 端到端解码回归测试（<b>C 组 MF-4</b>）。
/// 固化 MFSmoke 阶段2 的 <b>DECODE PASS</b> 验证：真实 H264 解码 MFT（<c>IMFTransform</c> vtable）
/// 经 <c>CoCreateInstance</c> 实例化，并透传 SPS+PPS（avcC）→ 输入类型 <c>MF_MT_MPEG_SEQUENCE_HEADER</c>。
/// 仅 Windows 且需系统注册 H264 解码 MFT（Windows 10/11 默认注册 CLSID_MSH264DecoderMFT）。
/// </summary>
[Trait("Category", "RequiresMediaFoundation")]
public sealed class MFVideoDecoderTests
{
    [Fact]
    public async Task DecodeAsync_EndToEnd_ProducesH264Frames()
    {
        // Arrange：打开 + 解封装取轨道与 SPS/PPS
        var backend = new MFBackend(NullLogger<MFBackend>.Instance);
        var source = new FileMediaSource(TestResources.VideoM1);
        var stream = new FileMediaStream(source);
        var demuxerFactory = new MFDemuxerFactory(backend, NullLoggerFactory.Instance);
        var demuxer = demuxerFactory.Create(stream);
        await demuxer.OpenAsync(stream, TestContext.Current.CancellationToken);

        var videoTrack = demuxer.Tracks.First(t => t.Type == TrackType.Video);
        videoTrack.VideoCodec.Should().NotBeNull();
        var vcodec = videoTrack.VideoCodec!.Value;
        var cfg = videoTrack.VideoInfo?.CodecConfiguration ?? default;

        int frames = 0;
        int firstW = 0, firstH = 0;
        long firstLen = 0;
        TimeSpan firstTs = default;

        try
        {
            // 解码器：透传 SPS+PPS（avcC）→ 输入类型 MF_MT_MPEG_SEQUENCE_HEADER
            var decoderFactory = new MFVideoDecoderFactory(NullLoggerFactory.Instance);
            var decoder = decoderFactory.Create(vcodec, new VideoSettings { CodecConfiguration = cfg });

            // 关键：解封装阶段已消费前若干包（含首个 IDR 关键帧）；
            // 不 Seek 回 0 则解码循环从非 IDR 处起步，H264 解码器无参考帧永远产不出帧。
            await demuxer.SeekAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

            int sent = 0;
            while (sent < 120 && frames < 5)
            {
                var packet = await demuxer.ReadPacketAsync(TestContext.Current.CancellationToken);
                if (packet is null) break;
                if (packet.TrackIndex != videoTrack.Index) { packet.Dispose(); continue; }
                sent++;

                var frame = await decoder.DecodeAsync(packet);
                packet.Dispose();
                if (frame is null) continue;

                frames++;
                if (frames == 1 && frame.Resource is SoftwareFrameResource sw)
                {
                    firstW = frame.Width;
                    firstH = frame.Height;
                    firstLen = sw.Data.Length;
                    firstTs = frame.Timestamp;
                }

                frame.Dispose();
            }

            var flushed = await decoder.FlushAsync();
            flushed?.Dispose();
            decoder.Dispose();

            // Assert：DECODE PASS
            frames.Should().BeGreaterThan(0, "MF 解码器应产出至少一帧");
            firstW.Should().BeGreaterThan(0, "首帧宽度应 > 0");
            firstH.Should().BeGreaterThan(0, "首帧高度应 > 0");
            firstTs.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero, "首帧时间戳应有效");

            // NV12 帧字节长度应 >= w*h*1.5（可能含行对齐余量）
            long expectedMin = (long)firstW * firstH * 3 / 2;
            firstLen.Should().BeGreaterThanOrEqualTo(expectedMin,
                $"首帧 NV12 数据长度应 >= {expectedMin}（w*h*1.5）");
        }
        finally
        {
            demuxer.Dispose();
            backend.Dispose();
        }
    }
}
