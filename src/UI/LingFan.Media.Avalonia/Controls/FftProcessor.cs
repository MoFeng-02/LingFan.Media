using System;
using System.Runtime.CompilerServices;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Cooley-Tukey 基-2 原地 FFT（AOT 友好，无反射、无堆分配热点）。
/// 仅供 <see cref="AudioVisualizer"/> 频谱分析使用。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：纯 CPU 计算，sync。无任何 I/O 或 await，补 async 即伪异步（禁止）。</para>
/// <para><b>AOT 兼容</b>：sealed 静态类，仅用 Span/Tuple 交换，无反射、无动态代码生成。</para>
/// <para><b>前置条件</b>：<c>real.Length</c> 与 <c>imag.Length</c> 必须相等且为 2 的幂；调用方负责满足。</para>
/// </remarks>
internal static class FftProcessor
{
    /// <summary>
    /// 原地正向 FFT（实数/虚数分量）。<paramref name="real"/> 与 <paramref name="imag"/> 原地被覆写为频域结果。
    /// </summary>
    /// <param name="real">实数分量（输入时域样本，输出频域实部）。长度须为 2 的幂。</param>
    /// <param name="imag">虚数分量（输入全零，输出频域虚部）。长度须等于 <paramref name="real"/>.Length。</param>
    public static void Forward(Span<float> real, Span<float> imag)
    {
        var n = real.Length;
        if (n <= 1) return;

        // 1. 位反转排列
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // 2. 蝶形运算（自底向上，len = 2,4,8,...）
        for (int len = 2; len <= n; len <<= 1)
        {
            // 单位旋转因子（负频率，正向 FFT）
            var ang = -2.0 * Math.PI / len;
            var wr = (float)Math.Cos(ang);
            var wi = (float)Math.Sin(ang);
            var half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (int k = 0; k < half; k++)
                {
                    var a = i + k;
                    var b = i + k + half;

                    var tr = real[b] * cr - imag[b] * ci;
                    var ti = real[b] * ci + imag[b] * cr;

                    real[b] = real[a] - tr;
                    imag[b] = imag[a] - ti;
                    real[a] += tr;
                    imag[a] += ti;

                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    /// <summary>
    /// 返回 ≥ <paramref name="value"/> 的最小 2 的幂，下限 8（避免极小 FFT 噪声放大）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextPow2(int value)
    {
        var p = 8;
        while (p < value) p <<= 1;
        return p;
    }
}
