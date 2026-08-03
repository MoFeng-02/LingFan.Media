using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// 无头视频 + 音频「正确性探针」（关 MF 冷启动调查篇用）。
/// 真实 MF 解码 m1.mp4 → 无头播放（NoOp 渲染 + 静音音频输出）→ 同时抓取视频 NV12 帧与音频 PCM，
/// 产出：① 解码帧联系表 PNG（肉眼确认画面非乱码/非空白）② 音频波形 PNG（肉眼确认有真实信号）
/// ③ JSON 指标报告；并做确定性断言（帧尺寸==轨尺寸、PTS 单调、亮度非-uniform、音频 RMS 非静音）。
/// 若多次运行均绿且视觉证据正常，即可判定无头视频/音频处理正确、本篇可翻篇。
/// </summary>
/// <remarks>
/// ⚠️ 须在本机（禁用沙盒）Windows + 已注册 H264 解码 MFT 下运行。
/// 容器：<c>AddLingFanMedia().AddMediaFoundation().AddHeadlessRenderer().AddSilentAudioOutput()</c>。
/// </remarks>
[Trait("Category", "RequiresMediaFoundation")]
public sealed class MediaCorrectnessProbeTests
{
    private readonly ITestOutputHelper _output;
    public MediaCorrectnessProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Probe_VideoM1_HeadlessDecode_VerifiesVideoAndAudioCorrectness()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddSilentAudioOutput();
        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // 抓取容器：回调内只做「拷贝」（帧为只读借用，sink 不得 Dispose）。后续按 Ts 排序还原顺序。
        var videoCaps = new ConcurrentBag<(byte[] Nv12, int W, int H, TimeSpan Ts)>();
        var audioCaps = new ConcurrentBag<(byte[] Pcm, int Rate, int Ch, SampleFormat Fmt, TimeSpan Ts)>();
        var videoCount = 0;
        var audioCount = 0;

        using var videoSink = new ProcessingFrameSink(onFrame: frame =>
        {
            Interlocked.Increment(ref videoCount);
            if (frame.Resource is SoftwareFrameResource sw && sw.Format == PixelFormat.NV12)
            {
                var buf = new byte[sw.Data.Length];
                sw.Data.Span.CopyTo(buf.AsSpan());
                videoCaps.Add((buf, frame.Width, frame.Height, frame.Timestamp));
            }
        });
        using var audioSink = new ProcessingAudioSink(onAudio: frame =>
        {
            Interlocked.Increment(ref audioCount);
            var buf = new byte[frame.Data.Length];
            frame.Data.Span.CopyTo(buf.AsSpan());
            audioCaps.Add((buf, frame.SampleRate, frame.Channels, frame.SampleFormat, frame.Timestamp));
        });

        var source = new FileMediaSource(TestResources.VideoM1);
        int sessVW = 0, sessVH = 0, sessAR = 0, sessACH = 0;
        bool hasAudio = false;
        try
        {
            videoSink.Attach(player);
            audioSink.Attach(player);
            await player.OpenAsync(source, TestContext.Current.CancellationToken);

            var session = player.Session!;
            hasAudio = session.AudioTracks.Count > 0;
            if (session.VideoTracks.Count > 0)
            {
                sessVW = session.VideoTracks[0].VideoInfo?.Width ?? 0;
                sessVH = session.VideoTracks[0].VideoInfo?.Height ?? 0;
            }
            if (hasAudio)
            {
                sessAR = session.AudioTracks[0].AudioInfo?.SampleRate ?? 0;
                sessACH = session.AudioTracks[0].AudioInfo?.Channels ?? 0;
            }

            await player.PlayAsync();

            // 让出式轮询：收到足够帧且 ~1.6s 无新帧视为 EOF；超时 75s 上限。
            const int pollMs = 200;
            int stableRounds = 0, lastTotal = -1;
            for (int i = 0; i < 375; i++)
            {
                await Task.Delay(pollMs, TestContext.Current.CancellationToken);
                int total = videoCount + audioCount;
                if (total == lastTotal) stableRounds++; else stableRounds = 0;
                lastTotal = total;
                if (videoCount >= 10 && (!hasAudio || audioCount >= 10) && stableRounds >= 8) break;
            }

            await player.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            videoSink.Detach();
            audioSink.Detach();
            await player.DisposeAsync();
        }

        // ===== 产出工件 + 指标 =====
        var diagDir = ResolveDiagnosticsDir();
        var sortedV = videoCaps.OrderBy(c => c.Ts).ToList();
        var sortedA = audioCaps.OrderBy(c => c.Ts).ToList();

        // 视频：联系表 + 亮度统计
        int lumaMin = 255, lumaMax = 0; long lumaSum = 0; long lumaN = 0; long lumaSumSq = 0;
        string? contactSheet = null;
        if (sortedV.Count > 0)
        {
            contactSheet = Path.Combine(diagDir, "contact_sheet.png");
            BuildContactSheet(sortedV, contactSheet, ref lumaMin, ref lumaMax, ref lumaSum, ref lumaN, ref lumaSumSq);
        }

        // 音频：波形 + RMS/Peak
        string? waveform = null;
        double audioRms = 0, audioPeak = 0;
        if (sortedA.Count > 0)
        {
            waveform = Path.Combine(diagDir, "audio_waveform.png");
            (audioRms, audioPeak) = BuildWaveform(sortedA, waveform);
        }

        // PTS 单调（非递减）
        bool ptsMonotonic = true;
        for (int i = 1; i < sortedV.Count; i++)
            if (sortedV[i].Ts < sortedV[i - 1].Ts) { ptsMonotonic = false; break; }

        // 帧尺寸一致且（若可得）== 轨尺寸
        bool dimsConsistent = sortedV.Count > 0 && sortedV.All(c => c.W == sortedV[0].W && c.H == sortedV[0].H);
        bool dimsMatchSession = sessVW <= 0 || (dimsConsistent && sortedV[0].W == sessVW && sortedV[0].H == sessVH);

        var report = new StringBuilder();
        report.AppendLine("{");
        report.AppendLine($"  \"videoFrames\": {videoCount},");
        report.AppendLine($"  \"audioFrames\": {audioCount},");
        report.AppendLine($"  \"hasAudio\": {(hasAudio ? "true" : "false")},");
        report.AppendLine($"  \"frameWidth\": {(sortedV.Count > 0 ? sortedV[0].W : 0)},");
        report.AppendLine($"  \"frameHeight\": {(sortedV.Count > 0 ? sortedV[0].H : 0)},");
        report.AppendLine($"  \"sessionVideoWidth\": {sessVW},");
        report.AppendLine($"  \"sessionVideoHeight\": {sessVH},");
        report.AppendLine($"  \"sessionAudioSampleRate\": {sessAR},");
        report.AppendLine($"  \"sessionAudioChannels\": {sessACH},");
        report.AppendLine($"  \"ptsMonotonic\": {(ptsMonotonic ? "true" : "false")},");
        report.AppendLine($"  \"dimsConsistent\": {(dimsConsistent ? "true" : "false")},");
        report.AppendLine($"  \"dimsMatchSession\": {(dimsMatchSession ? "true" : "false")},");
        report.AppendLine($"  \"lumaMin\": {lumaMin},");
        report.AppendLine($"  \"lumaMax\": {lumaMax},");
        double lumaMean = lumaN > 0 ? (double)lumaSum / lumaN : 0;
        double lumaStd = (lumaN > 0 && lumaN > 1) ? Math.Sqrt((double)lumaSumSq / lumaN - lumaMean * lumaMean) : 0;
        report.AppendLine($"  \"lumaMean\": {lumaMean:F2},");
        report.AppendLine($"  \"lumaStd\": {lumaStd:F2},");
        report.AppendLine($"  \"audioSampleRate\": {(sortedA.Count > 0 ? sortedA[0].Rate : 0)},");
        report.AppendLine($"  \"audioChannels\": {(sortedA.Count > 0 ? sortedA[0].Ch : 0)},");
        report.AppendLine($"  \"audioSampleFormat\": \"{JsonStr(sortedA.Count > 0 ? sortedA[0].Fmt.ToString() : "")}\",");
        report.AppendLine($"  \"audioRms\": {audioRms:E6},");
        report.AppendLine($"  \"audioPeak\": {audioPeak:E6},");
        report.AppendLine($"  \"artifacts\": {{ \"contactSheet\": \"{JsonStr(contactSheet ?? "")}\", \"waveform\": \"{JsonStr(waveform ?? "")}\" }},");
        report.AppendLine($"  \"generatedAt\": \"{JsonStr(DateTime.Now.ToString("O"))}\"");
        report.AppendLine("}");
        var jsonPath = Path.Combine(diagDir, "correctness_report.json");
        await File.WriteAllTextAsync(jsonPath, report.ToString(), TestContext.Current.CancellationToken);

        _output.WriteLine($"[PROBE] videoFrames={videoCount} audioFrames={audioCount} hasAudio={hasAudio}");
        int probeW = sortedV.Count > 0 ? sortedV[0].W : 0;
        int probeH = sortedV.Count > 0 ? sortedV[0].H : 0;
        int probeAR = sortedA.Count > 0 ? sortedA[0].Rate : 0;
        int probeACH = sortedA.Count > 0 ? sortedA[0].Ch : 0;
        _output.WriteLine($"[PROBE] frameSize={probeW}x{probeH} session={sessVW}x{sessVH}");
        _output.WriteLine($"[PROBE] ptsMonotonic={ptsMonotonic} dimsMatchSession={dimsMatchSession} luma=[{lumaMin},{lumaMax}] lumaStd={lumaStd:F2}");
        _output.WriteLine($"[PROBE] audio: {probeAR}Hz/{probeACH}ch RMS={audioRms:E3} Peak={audioPeak:E3}");
        _output.WriteLine($"[PROBE] artifacts: {contactSheet} | {waveform} | {jsonPath}");

        // ===== 确定性断言 =====
        videoCount.Should().BeGreaterThan(0, "无头视频应产出帧");
        if (hasAudio)
            audioCount.Should().BeGreaterThan(0, "文件含音轨，无头音频应产出 PCM 帧");

        dimsConsistent.Should().BeTrue("所有视频帧尺寸应一致");
        dimsMatchSession.Should().BeTrue("视频帧尺寸应与解封装轨尺寸一致");
        ptsMonotonic.Should().BeTrue("视频帧 PTS 应单调非递减（无乱序/重复）");

        // 亮度跨采样帧应存在真实变化（非统一空白、非纯黑/纯白垃圾）
        (lumaMax - lumaMin).Should().BeGreaterThan(8,
            "解码帧亮度应存在真实变化（证明是真实画面而非空白/乱码）");

        if (hasAudio)
            audioRms.Should().BeGreaterThan(1e-4,
                "音频 RMS 应 > 0（证明是真实信号而非静音/全零）");

        _output.WriteLine("[PROBE] ✅ 全部正确性断言通过");
    }

    // ---------- 联系表 ----------
    private static void BuildContactSheet(
        List<(byte[] Nv12, int W, int H, TimeSpan Ts)> frames,
        string path, ref int lumaMin, ref int lumaMax, ref long lumaSum, ref long lumaN, ref long lumaSumSq)
    {
        int count = Math.Min(frames.Count, 12);
        var step = frames.Count / count;
        var picked = new List<(byte[] Nv12, int W, int H)>();
        for (int i = 0; i < count; i++)
        {
            var f = frames[i * step];
            picked.Add((f.Nv12, f.W, f.H));
        }

        int cellW = picked.Min(p => p.W);
        cellW = Math.Min(cellW, 320);
        int cellH = (int)Math.Round(picked[0].H * (double)cellW / picked[0].W);
        int cols = Math.Min(4, count);
        int rows = (int)Math.Ceiling((double)count / cols);
        int sheetW = cols * cellW, sheetH = rows * cellH;

        var sheet = new byte[sheetW * sheetH * 4];
        for (int i = 0; i < picked.Count; i++)
        {
            var (nv12, w, h) = picked[i];
            var full = new byte[w * h * 4];
            Nv12ToRgba(nv12, w, h, full, ref lumaMin, ref lumaMax, ref lumaSum, ref lumaN, ref lumaSumSq);
            var cell = ScaleRgbaBox(full, w, h, cellW, cellH);
            int cx = (i % cols) * cellW;
            int cy = (i / cols) * cellH;
            Blit(cell, cellW, cellH, sheet, sheetW, sheetH, cx, cy);
        }
        Png.EncodeRgba(path, sheetW, sheetH, sheet);
    }

    // ---------- 波形 ----------
    private static (double rms, double peak) BuildWaveform(
        List<(byte[] Pcm, int Rate, int Ch, SampleFormat Fmt, TimeSpan Ts)> audios, string path)
    {
        const int maxSamples = 600_000;
        var mono = new float[maxSamples];
        int n = 0;
        foreach (var a in audios)
        {
            if (n >= maxSamples) break;
            AppendMono(a.Pcm, a.Fmt, a.Ch, mono, ref n, maxSamples);
        }

        double sumSq = 0, peak = 0;
        for (int i = 0; i < n; i++)
        {
            double v = mono[i];
            sumSq += v * v;
            double av = Math.Abs(v);
            if (av > peak) peak = av;
        }
        double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;

        const int W = 800, H = 200, mid = H / 2;
        var img = new byte[W * H * 4];
        // 背景（深灰）
        for (int p = 0; p < W * H; p++)
        {
            img[p * 4] = 24; img[p * 4 + 1] = 26; img[p * 4 + 2] = 32; img[p * 4 + 3] = 255;
        }
        if (n > 0)
        {
            for (int c = 0; c < W; c++)
            {
                int s0 = (int)((long)c * n / W);
                int s1 = (int)((long)(c + 1) * n / W);
                if (s1 <= s0) s1 = s0 + 1;
                double colPeak = 0;
                for (int s = s0; s < s1 && s < n; s++)
                {
                    double av = Math.Abs(mono[s]);
                    if (av > colPeak) colPeak = av;
                }
                int yHalf = (int)(colPeak * (mid - 2));
                if (yHalf > mid - 2) yHalf = mid - 2;
                for (int y = mid - yHalf; y <= mid + yHalf; y++)
                {
                    if (y < 0 || y >= H) continue;
                    int o = (y * W + c) * 4;
                    img[o] = 80; img[o + 1] = 200; img[o + 2] = 140; img[o + 3] = 255;
                }
            }
        }
        Png.EncodeRgba(path, W, H, img);
        return (rms, peak);
    }

    // ---------- 像素工具 ----------
    private static void Nv12ToRgba(ReadOnlySpan<byte> nv12, int w, int h, Span<byte> rgba,
        ref int lumaMin, ref int lumaMax, ref long lumaSum, ref long lumaN, ref long lumaSumSq)
    {
        int ySize = w * h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int yi = y * w + x;
                int ui = ySize + (y / 2) * w + (x / 2) * 2;
                int Y = nv12[yi], U = nv12[ui], V = nv12[ui + 1];
                int c = Y - 16, d = U - 128, e = V - 128;
                int r = Clamp((298 * c + 409 * e + 128) >> 8);
                int g = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                int b = Clamp((298 * c + 516 * d + 128) >> 8);
                int o = yi * 4;
                rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)b; rgba[o + 3] = 255;

                if (Y < lumaMin) lumaMin = Y;
                if (Y > lumaMax) lumaMax = Y;
                lumaSum += Y; lumaSumSq += (long)Y * Y; lumaN++;
            }
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    // 面积平均(box)下采样：每个目标像素覆盖源矩形内所有像素求均值。
    // 相比最近邻，能正确代表原图、消除 6x 缩小产生的混叠「雪花噪点」。
    private static byte[] ScaleRgbaBox(ReadOnlySpan<byte> src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int sy0 = y * sh / dh;
            int sy1 = (y + 1) * sh / dh;
            if (sy1 <= sy0) sy1 = sy0 + 1;
            if (sy1 > sh) sy1 = sh;
            for (int x = 0; x < dw; x++)
            {
                int sx0 = x * sw / dw;
                int sx1 = (x + 1) * sw / dw;
                if (sx1 <= sx0) sx1 = sx0 + 1;
                if (sx1 > sw) sx1 = sw;
                long sr = 0, sg = 0, sb = 0; int cnt = 0;
                for (int sy = sy0; sy < sy1; sy++)
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int si = (sy * sw + sx) * 4;
                        sr += src[si]; sg += src[si + 1]; sb += src[si + 2];
                        cnt++;
                    }
                if (cnt == 0) cnt = 1;
                int di = (y * dw + x) * 4;
                dst[di] = (byte)(sr / cnt);
                dst[di + 1] = (byte)(sg / cnt);
                dst[di + 2] = (byte)(sb / cnt);
                dst[di + 3] = 255;
            }
        }
        return dst;
    }

    private static void Blit(byte[] cell, int cw, int ch, byte[] sheet, int sw, int sh, int ox, int oy)
    {
        for (int y = 0; y < ch; y++)
        {
            int dy = oy + y;
            if (dy < 0 || dy >= sh) continue;
            for (int x = 0; x < cw; x++)
            {
                int dx = ox + x;
                if (dx < 0 || dx >= sw) continue;
                int si = (y * cw + x) * 4;
                int di = (dy * sw + dx) * 4;
                sheet[di] = cell[si]; sheet[di + 1] = cell[si + 1];
                sheet[di + 2] = cell[si + 2]; sheet[di + 3] = 255;
            }
        }
    }

    private static void AppendMono(ReadOnlySpan<byte> pcm, SampleFormat fmt, int channels,
        float[] acc, ref int accLen, int maxLen)
    {
        int bps = fmt switch { SampleFormat.S16 => 2, SampleFormat.S32 => 4, SampleFormat.F32 => 4, _ => 2 };
        int frames = pcm.Length / (bps * channels);
        for (int i = 0; i < frames && accLen < maxLen; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int off = (i * channels + c) * bps;
                float s = bps switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(off)) / 32768f,
                    4 when fmt == SampleFormat.F32 => BinaryPrimitives.ReadSingleLittleEndian(pcm.Slice(off)),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(pcm.Slice(off)) / 2147483648f
                };
                sum += s;
            }
            acc[accLen++] = sum / channels;
        }
    }

    // ---------- 目录解析 ----------
    private static string ResolveDiagnosticsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LingFan.Media.slnx")))
            dir = dir.Parent;
        var outDir = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "TestInfo", "Diagnostics");
        Directory.CreateDirectory(outDir);
        return outDir;
    }

    private static string JsonStr(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------- 极简 PNG 编码器（RGBA / color type 6，zlib 经 BCL ZLibStream） ----------
    private static class Png
    {
        private static readonly uint[] CrcTable = BuildCrc();

        internal static void EncodeRgba(string path, int w, int h, ReadOnlySpan<byte> rgba)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
            ihdr[8] = 0x08; ihdr[9] = 0x06;
            WriteChunk(fs, "IHDR", ihdr);

            using var ms = new MemoryStream();
            using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[w * 4 + 1];
                row[0] = 0;
                for (int y = 0; y < h; y++)
                {
                    rgba.Slice(y * w * 4, w * 4).CopyTo(row.AsSpan(1));
                    zs.Write(row);
                }
            }
            WriteChunk(fs, "IDAT", ms.ToArray());
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteChunk(Stream fs, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            fs.Write(len);
            fs.Write(typeBytes);
            fs.Write(data);
            uint crc = Crc(typeBytes, data);
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
            fs.Write(crcBuf);
        }

        private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < a.Length; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ a[i]) & 0xFF];
            for (int i = 0; i < b.Length; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ b[i]) & 0xFF];
            return crc ^ 0xFFFFFFFF;
        }

        private static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
    }
}
