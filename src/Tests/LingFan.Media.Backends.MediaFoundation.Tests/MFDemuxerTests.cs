using System.Linq;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.MediaFoundation.Demuxer;
using LingFan.Media.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// MediaFoundation 解封装路径回归测试（<b>不依赖解码 MFT</b>）。
/// 固化 MFSmoke 阶段1 验证：<c>ReadSample</c> 槽位 + <c>IMFSample.ConvertToContiguousBuffer</c> 槽位有效。
/// 仅 Windows（MediaFoundation 平台 API）。
/// </summary>
[Trait("Category", "RequiresMediaFoundation")]
public sealed class MFDemuxerTests
{
    private static async Task<(IMediaDemuxer Demuxer, MediaTrack VideoTrack)> OpenAsync()
    {
        var backend = new MFBackend(NullLogger<MFBackend>.Instance);
        var source = new FileMediaSource(TestResources.VideoM1);
        var stream = new FileMediaStream(source);
        var factory = new MFDemuxerFactory(backend, NullLoggerFactory.Instance);
        var demuxer = factory.Create(stream);
        await demuxer.OpenAsync(stream, TestContext.Current.CancellationToken);
        var videoTrack = demuxer.Tracks.First(t => t.Type == TrackType.Video);
        return (demuxer, videoTrack);
    }

    [Fact]
    public async Task OpenAsync_WithM1Mp4_EnumeratesVideoTrack()
    {
        var (demuxer, videoTrack) = await OpenAsync();

        try
        {
            demuxer.Tracks.Should().NotBeEmpty("应至少枚举到一个轨道");

            videoTrack.Should().NotBeNull();
            videoTrack.Type.Should().Be(TrackType.Video);
            videoTrack.VideoCodec.Should().NotBeNull("视频轨道应有编解码器信息");
            videoTrack.VideoInfo.Should().NotBeNull();
            videoTrack.VideoInfo!.Width.Should().BeGreaterThan(0, "视频宽度应 > 0");
            videoTrack.VideoInfo.Height.Should().BeGreaterThan(0, "视频高度应 > 0");
        }
        finally
        {
            demuxer.Dispose();
        }
    }

    [Fact]
    public async Task ReadPacketAsync_WithVideoStream_ReturnsNonEmptyPackets()
    {
        var (demuxer, videoTrack) = await OpenAsync();

        try
        {
            int readPackets = 0, videoPackets = 0, nonEmpty = 0;
            long maxVideoLen = 0;

            while (readPackets < 60)
            {
                var packet = await demuxer.ReadPacketAsync(TestContext.Current.CancellationToken);
                if (packet is null) break;
                readPackets++;

                if (packet.TrackIndex == videoTrack.Index)
                {
                    videoPackets++;
                    if (packet.Data.Length > 0) nonEmpty++;
                    if (packet.Data.Length > maxVideoLen) maxVideoLen = packet.Data.Length;
                }

                packet.Dispose();
            }

            // 解封装读取路径 PASS：ReadSample 槽位 + IMFSample.ConvertToContiguousBuffer 槽位有效
            readPackets.Should().BeGreaterThan(0, "应能读取到压缩包");
            videoPackets.Should().BeGreaterThan(0, "应含视频包");
            nonEmpty.Should().Be(videoPackets, "所有视频包应为非空压缩数据");
            maxVideoLen.Should().BeGreaterThan(0, "视频包应有正字节长度");
        }
        finally
        {
            demuxer.Dispose();
        }
    }
}
