using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.D3D11;

/// <summary>
/// D3D11 渲染器的<b>环境变量门控</b>诊断设施（默认全关，零运行时开销）。
/// </summary>
/// <remarks>
/// <para><b>用途</b>：把「GPU 着色完成、但尚未 Present」的 backbuffer 回读落盘，
/// 用于一刀切开问题空间——</para>
/// <list type="bullet">
/// <item>backbuffer <b>干净</b> ⇒ 上传 + Shader 路径无辜，责任在<b>呈现/合成侧</b>
/// （SwapChain / DirectComposition / DWM / 帧节奏）。</item>
/// <item>backbuffer <b>脏</b> ⇒ 责任在<b>上传/Shader 路径</b>，与合成无关。</item>
/// </list>
/// <para><b>开关</b>（均为环境变量，未设置即关闭）：</para>
/// <list type="table">
/// <item><term>LINGFAN_D3D11_DUMP</term><description>落盘前 N 帧 backbuffer（BMP）。</description></item>
/// <item><term>LINGFAN_D3D11_DUMP_DIR</term><description>落盘目录（默认 %CD%/TestInfo/Diagnostics/backbuffer）。</description></item>
/// <item><term>LINGFAN_D3D11_FORCE_HWND</term><description>=1 时跳过 DirectComposition，强制 CreateSwapChainForHwnd。</description></item>
/// <item><term>LINGFAN_D3D11_SYNC</term><description>Present 的 SyncInterval（默认 1）。0 = 不等 VSync。</description></item>
/// </list>
/// <para><b>AOT 兼容</b>：静态类，无反射，P/Invoke 用 <c>[LibraryImport]</c>，<c>unsafe</c> 落方法级。</para>
/// <para><b>线程安全</b>：调用方（<see cref="D3D11Renderer"/>）持 <c>_gate</c> 锁，本类不再自锁。</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class D3D11Diagnostics
{
    // ── 环境变量门控（进程内只读一次）──

    /// <summary>=1 时跳过 DirectComposition，强制 HWND SwapChain（用于二分合成层责任）。</summary>
    internal static readonly bool ForceHwnd =
        string.Equals(Environment.GetEnvironmentVariable("LINGFAN_D3D11_FORCE_HWND"), "1", StringComparison.Ordinal);

    /// <summary>Present 的 SyncInterval，默认 1（等 VSync）。设 0 可二分「阻塞式 Present 造成的卡顿/延迟」。</summary>
    internal static readonly uint SyncInterval = ParseUInt("LINGFAN_D3D11_SYNC", 1u);

    /// <summary>需要落盘的 backbuffer 帧数，0 = 关闭。</summary>
    internal static readonly int DumpCount = (int)ParseUInt("LINGFAN_D3D11_DUMP", 0u);

    /// <summary>
    /// 开始落盘前先跳过的 Present 次数，默认 0。
    /// <para>用途：消除「只抓到开头几帧」的采样偏差。默认从第 1 帧起连抓 N 帧，
    /// 全部落在视频头 1 秒内；若画质问题在中后段才显现（运动剧烈场景、码率切换、GOP 边界），
    /// 前几帧一律拍不到，会得出「一切正常」的错误结论。</para>
    /// </summary>
    internal static readonly int DumpSkip = (int)ParseUInt("LINGFAN_D3D11_DUMP_SKIP", 0u);

    /// <summary>落盘间隔：每 N 次 Present 抓 1 帧，默认 1（连续抓）。设大可让样本跨越更长时间。</summary>
    internal static readonly int DumpEvery = Math.Max(1, (int)ParseUInt("LINGFAN_D3D11_DUMP_EVERY", 1u));

    /// <summary>落盘目录。</summary>
    internal static readonly string DumpDir =
        Environment.GetEnvironmentVariable("LINGFAN_D3D11_DUMP_DIR")
        ?? Path.Combine(Environment.CurrentDirectory, "TestInfo", "Diagnostics", "backbuffer");

    private static uint ParseUInt(string name, uint fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : fallback;
    }

    // ── 线程归属判定（DirectComposition 要求视觉树操作与窗口线程一致）──

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    /// <summary>返回「窗口所属线程 ID」与「当前线程 ID」，供 Attach 期一次性诊断日志使用。</summary>
    internal static (uint WindowThread, uint CurrentThread) InspectThreadAffinity(IntPtr hwnd)
        => (GetWindowThreadProcessId(hwnd, IntPtr.Zero), GetCurrentThreadId());

    // ── BackBuffer 回读 ──

    /// <summary>
    /// 把 backbuffer 回读到 CPU 并落 32bpp BMP，同时算出<b>眼睛无关</b>的客观指标。
    /// </summary>
    /// <returns>一行可直接打日志的统计摘要。</returns>
    internal static unsafe string DumpBackBuffer(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D backBuffer, int index)
    {
        var desc = backBuffer.Description;
        int w = (int)desc.Width;
        int h = (int)desc.Height;
        if (w <= 0 || h <= 0) return $"#{index} 尺寸无效 {w}x{h}";

        // 与 backbuffer 同描述的 STAGING 副本（唯一改动：Usage/BindFlags/CPUAccess/Misc）
        var stagingDesc = desc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDesc.MiscFlags = ResourceOptionFlags.None;

        using ID3D11Texture2D staging = device.CreateTexture2D(stagingDesc);
        context.CopyResource(staging, backBuffer);

        byte[] bmp = new byte[54 + (w * h * 4)];
        byte[] luma = new byte[w * h];

        var mapped = context.Map(staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int srcPitch = (int)mapped.RowPitch;
            byte* src = (byte*)mapped.DataPointer;

            WriteBmpHeader(bmp, w, h);
            int dstOffset = 54;

            for (int y = 0; y < h; y++)
            {
                byte* row = src + ((long)y * srcPitch);
                int dstRow = dstOffset + (y * w * 4);
                int lumaRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte b = row[(x * 4) + 0];
                    byte g = row[(x * 4) + 1];
                    byte r = row[(x * 4) + 2];
                    bmp[dstRow + (x * 4) + 0] = b;
                    bmp[dstRow + (x * 4) + 1] = g;
                    bmp[dstRow + (x * 4) + 2] = r;
                    bmp[dstRow + (x * 4) + 3] = 255; // 强制不透明，避免看图器按 alpha 渲染成全黑
                    luma[lumaRow + x] = (byte)(((77 * r) + (150 * g) + (29 * b)) >> 8);
                }
            }
        }
        finally
        {
            context.Unmap(staging, 0u);
        }

        Directory.CreateDirectory(DumpDir);
        string path = Path.Combine(DumpDir, $"backbuffer_{index:D5}.bmp");
        File.WriteAllBytes(path, bmp);

        (double mean, double std) = LumaStats(luma);
        (int skew, int agree, int total, double ratio) = RowSkew(luma, w, h);

        string verdict = skew == 0
            ? "行对齐正常"
            : $"★行错位 d={skew}★";

        return $"#{index} {w}x{h} luma均值={mean:F1} 标准差={std:F2} | " +
               $"行间最佳位移 d={skew}（{agree}/{total} 行一致）SAD比={ratio:F4} => {verdict} | {path}" +
               Environment.NewLine + "           " + DirectionalFreq(luma, w, h);
    }

    /// <summary>
    /// 方向性高频能量（与 b2 <c>ImageUtil.AnalyzePlane</c> <b>同判据同口径</b>，可两侧直接对比）：
    /// 水平/垂直相邻像素平均绝对差 + 奇偶列/奇偶行均值差。
    /// </summary>
    /// <remarks>
    /// H/V &gt; 1.6 ⇒ 竖条纹；&lt; 0.6 ⇒ 横条纹/隔行；奇偶列差 &gt; 2.0 ⇒ 1px 竖向交替。
    /// 用途：b2（解码输出）与 b3（backbuffer）跑同一把尺子，
    /// 数值接近 ⇒ 脏是从解码带进来的；b3 显著变差 ⇒ 脏是渲染引入的。
    /// </remarks>
    private static string DirectionalFreq(byte[] p, int w, int h)
    {
        long hSum = 0, hCnt = 0, vSum = 0, vCnt = 0;
        long ec = 0, oc = 0, ecN = 0, ocN = 0;
        long er = 0, orr = 0, erN = 0, orN = 0;

        for (int y = 0; y < h; y++)
        {
            int rb = y * w;
            for (int x = 0; x < w; x++)
            {
                int v = p[rb + x];
                if ((x & 1) == 0) { ec += v; ecN++; } else { oc += v; ocN++; }
                if ((y & 1) == 0) { er += v; erN++; } else { orr += v; orN++; }
                if (x + 1 < w) { hSum += Math.Abs(p[rb + x + 1] - v); hCnt++; }
                if (y + 1 < h) { vSum += Math.Abs(p[rb + w + x] - v); vCnt++; }
            }
        }

        double hf = hCnt > 0 ? (double)hSum / hCnt : 0;
        double vf = vCnt > 0 ? (double)vSum / vCnt : 0;
        double ratio = vf > 0.0001 ? hf / vf : 0;
        double colDiff = Math.Abs((ecN > 0 ? (double)ec / ecN : 0) - (ocN > 0 ? (double)oc / ocN : 0));
        double rowDiff = Math.Abs((erN > 0 ? (double)er / erN : 0) - (orN > 0 ? (double)orr / orN : 0));

        string v2 = ratio > 1.6 ? "竖条纹" : ratio < 0.6 ? "横条纹/隔行" : "方向性正常";
        if (colDiff > 2.0) v2 += $" +1px竖向交替({colDiff:F2})";
        if (rowDiff > 2.0) v2 += $" +1px横向交替({rowDiff:F2})";

        return $"[FREQ-BB] 水平高频={hf:F3} 垂直高频={vf:F3} H/V={ratio:F3} | " +
               $"奇偶列差={colDiff:F2} 奇偶行差={rowDiff:F2} => {v2}";
    }

    private static void WriteBmpHeader(byte[] bmp, int w, int h)
    {
        var span = bmp.AsSpan();
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], 54);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], w);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], -h);   // 负高 = 自上而下
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);    // planes
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 32);   // bpp
        BinaryPrimitives.WriteInt32LittleEndian(span[30..], 0);    // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], w * h * 4);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(span[46..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[50..], 0);
    }

    private static (double Mean, double Std) LumaStats(byte[] luma)
    {
        long sum = 0;
        for (int i = 0; i < luma.Length; i++) sum += luma[i];
        double mean = (double)sum / luma.Length;

        double acc = 0;
        for (int i = 0; i < luma.Length; i++)
        {
            double d = luma[i] - mean;
            acc += d * d;
        }
        return (mean, Math.Sqrt(acc / luma.Length));
    }

    /// <summary>
    /// 行间水平错位检测（与 b2 <c>[SKEW-CHK]</c> 同算法，眼睛无关）。
    /// </summary>
    /// <remarks>
    /// 自然图像相邻两行高度相关 ⇒ 把下一行水平平移 d 去匹配上一行，最佳 d 必为 0。
    /// 若 stride / rowPitch 假定错误，每行会恒定平移 <c>d = 假定stride - 真实stride</c>。
    /// </remarks>
    private static (int Skew, int Agree, int Total, double Ratio) RowSkew(byte[] luma, int w, int h)
    {
        const int MaxShift = 48;
        const int RowStep = 17;   // 质数步长，避开周期性纹理
        if (w <= (MaxShift * 2) + 16 || h < 4) return (0, 0, 0, 1.0);

        int x0 = MaxShift;
        int x1 = w - MaxShift;
        Span<int> votes = stackalloc int[(MaxShift * 2) + 1];
        int total = 0;
        double sumBest = 0, sumZero = 0;

        for (int y = RowStep; y < h; y += RowStep)
        {
            int prev = (y - 1) * w;
            int cur = y * w;
            long best = long.MaxValue;
            int bestD = 0;
            long zeroSad = 0;

            for (int d = -MaxShift; d <= MaxShift; d++)
            {
                long sad = 0;
                for (int x = x0; x < x1; x += 2)   // 隔点采样，够用且快一倍
                    sad += Math.Abs(luma[cur + x + d] - luma[prev + x]);

                if (d == 0) zeroSad = sad;
                if (sad < best) { best = sad; bestD = d; }
            }

            votes[bestD + MaxShift]++;
            total++;
            sumBest += best;
            sumZero += zeroSad;
        }

        if (total == 0) return (0, 0, 0, 1.0);

        int mode = 0, modeCount = -1;
        for (int i = 0; i < votes.Length; i++)
        {
            if (votes[i] > modeCount) { modeCount = votes[i]; mode = i - MaxShift; }
        }

        double ratio = sumZero > 0 ? sumBest / sumZero : 1.0;
        return (mode, modeCount, total, ratio);
    }
}
