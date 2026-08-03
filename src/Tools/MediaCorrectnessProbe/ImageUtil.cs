using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LingFan.Media.Abstractions;

namespace MediaCorrectnessProbe;

/// <summary>
/// 零依赖的像素/PCM 工具：NV12→RGBA、面积平均下采样、拼贴、极简 PNG 编码、PCM→单声道浮点。
/// </summary>
/// <remarks>
/// 与 <c>MediaCorrectnessProbeTests</c> 中的同名实现保持一致（刻意复制而非抽公共库：
/// 最小程序应<b>自包含</b>，避免共享层变更把探针一起带偏，削弱其"独立第三方证据"的地位）。
/// </remarks>
internal static class ImageUtil
{
    /// <summary>亮度统计累加器。</summary>
    internal sealed class LumaStats
    {
        public int Min = 255;
        public int Max;
        public long Sum;
        public long SumSq;
        public long Count;

        public double Mean => Count > 0 ? (double)Sum / Count : 0;
        public double Std => Count > 1 ? Math.Sqrt(Math.Max(0, (double)SumSq / Count - Mean * Mean)) : 0;
    }

    /// <summary>把若干帧拼成联系表 PNG（最多 12 格，4 列），同时累计亮度统计。</summary>
    internal static void BuildContactSheet(
        List<(byte[] Nv12, int W, int H, TimeSpan Ts)> frames, string path, LumaStats luma)
    {
        int count = Math.Min(frames.Count, 12);
        int step = Math.Max(1, frames.Count / count);
        var picked = new List<(byte[] Nv12, int W, int H)>();
        for (int i = 0; i < count; i++)
        {
            var f = frames[Math.Min(i * step, frames.Count - 1)];
            picked.Add((f.Nv12, f.W, f.H));
        }

        int cellW = Math.Min(picked.Min(p => p.W), 320);
        int cellH = (int)Math.Round(picked[0].H * (double)cellW / picked[0].W);
        int cols = Math.Min(4, count);
        int rows = (int)Math.Ceiling((double)count / cols);
        int sheetW = cols * cellW, sheetH = rows * cellH;

        var sheet = new byte[sheetW * sheetH * 4];
        for (int i = 0; i < picked.Count; i++)
        {
            var (nv12, w, h) = picked[i];
            var full = new byte[w * h * 4];
            Nv12ToRgba(nv12, w, h, full, luma);
            var cell = ScaleRgbaBox(full, w, h, cellW, cellH);
            Blit(cell, cellW, cellH, sheet, sheetW, sheetH, (i % cols) * cellW, (i / cols) * cellH);
        }
        Png.EncodeRgba(path, sheetW, sheetH, sheet);
    }

    /// <summary>绘制音频波形 PNG（峰值包络），返回整体 RMS 与峰值（归一化 ±1.0）。</summary>
    internal static (double Rms, double Peak) BuildWaveform(
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

        const int W = 1000, H = 220, mid = H / 2;
        var img = new byte[W * H * 4];
        for (int p = 0; p < W * H; p++)
        {
            img[p * 4] = 24; img[p * 4 + 1] = 26; img[p * 4 + 2] = 32; img[p * 4 + 3] = 255;
        }
        // 中线（便于肉眼看出"整段静音"与"有信号"）
        for (int c = 0; c < W; c++)
        {
            int o = (mid * W + c) * 4;
            img[o] = 70; img[o + 1] = 74; img[o + 2] = 84;
        }
        if (n > 0)
        {
            for (int c = 0; c < W; c++)
            {
                int s0 = (int)((long)c * n / W);
                int s1 = Math.Max(s0 + 1, (int)((long)(c + 1) * n / W));
                double colPeak = 0;
                for (int s = s0; s < s1 && s < n; s++)
                {
                    double av = Math.Abs(mono[s]);
                    if (av > colPeak) colPeak = av;
                }
                int yHalf = Math.Min((int)(colPeak * (mid - 2)), mid - 2);
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

    internal static void Nv12ToRgba(ReadOnlySpan<byte> nv12, int w, int h, Span<byte> rgba, LumaStats luma)
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
                int o = yi * 4;
                rgba[o] = (byte)Clamp((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;

                if (Y < luma.Min) luma.Min = Y;
                if (Y > luma.Max) luma.Max = Y;
                luma.Sum += Y;
                luma.SumSq += (long)Y * Y;
                luma.Count++;
            }
        }
    }

    /// <summary>
    /// 纯 Y 平面 → 灰度 RGBA（完全绕开色度）。
    /// 用途：把「亮度」与「色度」的责任切开——若灰度图干净而彩色图脏，责任 100% 在色度平面/上采样。
    /// </summary>
    internal static void YPlaneToRgba(ReadOnlySpan<byte> nv12, int w, int h, Span<byte> rgba)
    {
        for (int i = 0; i < w * h; i++)
        {
            byte v = nv12[i];
            int o = i * 4;
            rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
        }
    }

    /// <summary>
    /// 按 1:1 原始分辨率落盘若干帧（<b>不做任何缩放</b>），每帧输出彩色 + 纯 Y 灰度两张。
    /// </summary>
    /// <remarks>
    /// 存在意义：contact_sheet 每格仅 320 宽（1906→320 = 5.96x 缩小），
    /// 在其上判断"有无污染物"等于在重采样产物上找证据。定位画质问题必须看原始像素。
    /// </remarks>
    internal static List<string> DumpFullFrames(
        List<(byte[] Nv12, int W, int H, TimeSpan Ts)> frames, string dir, int count)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        int n = Math.Min(count, frames.Count);
        if (n <= 0) return written;

        int step = Math.Max(1, frames.Count / n);
        var throwaway = new LumaStats();
        for (int i = 0; i < n; i++)
        {
            var f = frames[Math.Min(i * step, frames.Count - 1)];
            int w = f.W, h = f.H;

            var rgba = new byte[w * h * 4];
            Nv12ToRgba(f.Nv12, w, h, rgba, throwaway);
            string p1 = Path.Combine(dir, $"full_{i:D2}_{f.Ts.TotalSeconds:F2}s_color.png");
            Png.EncodeRgba(p1, w, h, rgba);
            written.Add(p1);

            var gray = new byte[w * h * 4];
            YPlaneToRgba(f.Nv12, w, h, gray);
            string p2 = Path.Combine(dir, $"full_{i:D2}_{f.Ts.TotalSeconds:F2}s_yplane.png");
            Png.EncodeRgba(p2, w, h, gray);
            written.Add(p2);
        }
        return written;
    }

    /// <summary>
    /// 方向性高频能量：分别统计<b>水平</b>与<b>垂直</b>相邻像素的平均绝对差，
    /// 外加<b>奇偶列/奇偶行</b>均值差（抓 1 像素周期的交替条纹）。
    /// </summary>
    /// <remarks>
    /// 判据（眼睛无关）：
    /// <list type="bullet">
    /// <item>自然图像水平/垂直高频大致相当，H/V 比通常落在 0.7~1.4。</item>
    /// <item><b>H/V &gt; 1.6 ⇒ 竖条纹</b>（水平方向异常高频）。</item>
    /// <item><b>H/V &lt; 0.6 ⇒ 横条纹</b>（典型隔行/梳状）。</item>
    /// <item>奇偶列差 &gt; 2.0 ⇒ 存在 1 像素周期竖向交替（色度上采样/半像素错位的经典特征）。</item>
    /// </list>
    /// </remarks>
    internal static string AnalyzePlane(ReadOnlySpan<byte> plane, int w, int h, string name)
    {
        long hSum = 0, hCnt = 0, vSum = 0, vCnt = 0;
        long evenColSum = 0, oddColSum = 0, evenColCnt = 0, oddColCnt = 0;
        long evenRowSum = 0, oddRowSum = 0, evenRowCnt = 0, oddRowCnt = 0;

        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int v = plane[rowBase + x];
                if ((x & 1) == 0) { evenColSum += v; evenColCnt++; } else { oddColSum += v; oddColCnt++; }
                if ((y & 1) == 0) { evenRowSum += v; evenRowCnt++; } else { oddRowSum += v; oddRowCnt++; }

                if (x + 1 < w) { hSum += Math.Abs(plane[rowBase + x + 1] - v); hCnt++; }
                if (y + 1 < h) { vSum += Math.Abs(plane[rowBase + w + x] - v); vCnt++; }
            }
        }

        double hf = hCnt > 0 ? (double)hSum / hCnt : 0;
        double vf = vCnt > 0 ? (double)vSum / vCnt : 0;
        double ratio = vf > 0.0001 ? hf / vf : 0;
        double colDiff = Math.Abs((evenColCnt > 0 ? (double)evenColSum / evenColCnt : 0) -
                                  (oddColCnt > 0 ? (double)oddColSum / oddColCnt : 0));
        double rowDiff = Math.Abs((evenRowCnt > 0 ? (double)evenRowSum / evenRowCnt : 0) -
                                  (oddRowCnt > 0 ? (double)oddRowSum / oddRowCnt : 0));

        string verdict = ratio > 1.6 ? "⚠ 竖条纹（水平高频异常）"
                       : ratio < 0.6 ? "⚠ 横条纹/隔行（垂直高频异常）"
                       : "方向性正常";
        if (colDiff > 2.0) verdict += $" + ⚠ 1px竖向交替(奇偶列差={colDiff:F2})";
        if (rowDiff > 2.0) verdict += $" + ⚠ 1px横向交替(奇偶行差={rowDiff:F2})";

        return $"[FREQ-{name}] {w}x{h} | 水平高频={hf:F3} 垂直高频={vf:F3} H/V={ratio:F3} | " +
               $"奇偶列差={colDiff:F2} 奇偶行差={rowDiff:F2} => {verdict}";
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>面积平均(box)下采样：消除最近邻在大比例缩小时产生的混叠"雪花"。</summary>
    internal static byte[] ScaleRgbaBox(ReadOnlySpan<byte> src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int sy0 = y * sh / dh;
            int sy1 = Math.Min(sh, Math.Max(sy0 + 1, (y + 1) * sh / dh));
            for (int x = 0; x < dw; x++)
            {
                int sx0 = x * sw / dw;
                int sx1 = Math.Min(sw, Math.Max(sx0 + 1, (x + 1) * sw / dw));
                long sr = 0, sg = 0, sb = 0;
                int cnt = 0;
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

    internal static void Blit(byte[] cell, int cw, int ch, byte[] sheet, int sw, int sh, int ox, int oy)
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

    /// <summary>把任意格式 PCM 混成单声道浮点（±1.0）追加到累加缓冲。</summary>
    internal static void AppendMono(ReadOnlySpan<byte> pcm, SampleFormat fmt, int channels,
        float[] acc, ref int accLen, int maxLen)
    {
        int bps = fmt switch { SampleFormat.S16 => 2, _ => 4 };
        int frames = pcm.Length / (bps * channels);
        for (int i = 0; i < frames && accLen < maxLen; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int off = (i * channels + c) * bps;
                sum += bps switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(pcm[off..]) / 32768f,
                    4 when fmt == SampleFormat.F32 => BinaryPrimitives.ReadSingleLittleEndian(pcm[off..]),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(pcm[off..]) / 2147483648f
                };
            }
            acc[accLen++] = sum / channels;
        }
    }

    /// <summary>把原始 PCM 写成标准 WAV（S16→PCM(1)，F32→IEEE_FLOAT(3)）。</summary>
    internal static void WriteWav(string path, byte[] pcm, int sampleRate, int channels, SampleFormat fmt)
    {
        int bps = fmt == SampleFormat.S16 ? 2 : 4;
        short audioFormat = fmt == SampleFormat.F32 ? (short)3 : (short)1;
        int blockAlign = channels * bps;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write(audioFormat); w.Write((short)channels); w.Write(sampleRate);
        w.Write(sampleRate * blockAlign); w.Write((short)blockAlign); w.Write((short)(bps * 8));
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
    }

    /// <summary>极简 PNG 编码器（RGBA / color type 6，zlib 走 BCL ZLibStream，无第三方依赖）。</summary>
    internal static class Png
    {
        private static readonly uint[] CrcTable = BuildCrc();

        internal static void EncodeRgba(string path, int w, int h, ReadOnlySpan<byte> rgba)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);

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
            WriteChunk(fs, "IEND", []);
        }

        private static void WriteChunk(Stream fs, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            fs.Write(len);
            fs.Write(typeBytes);
            fs.Write(data);
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc(typeBytes, data));
            fs.Write(crcBuf);
        }

        private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte x in a) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
            foreach (byte x in b) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
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
