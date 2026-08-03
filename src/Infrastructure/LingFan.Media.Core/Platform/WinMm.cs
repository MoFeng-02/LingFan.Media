namespace LingFan.Media.Core.Platform;

/// <summary>
/// winmm.dll 高精度系统定时器互操作（仅 Windows）。
/// </summary>
/// <remarks>
/// <para><see cref="TimeBeginPeriod"/> 将系统定时器分辨率提高到 <paramref name="ms"/> 毫秒，
/// 使 <see cref="System.Threading.Thread.Sleep"/> 的粒度从默认 15.6ms 降到 ~1ms ——
/// 这是视频帧精确等待（frame pacing）消抖的必要前提：否则 <c>Thread.Sleep(1)</c> 实际睡 1~15.6ms，
/// 本身即 ±15ms 量级的抖动源（见 <c>[PACING]</c> 的 Sleep 项，基线测到均值 14.13ms）。</para>
/// <para><b>必须对称调用 <see cref="TimeEndPeriod"/> 还原</b>，否则整机定时器维持高精度（影响功耗）。
/// 由 <see cref="MediaPlayer"/> 在 Play/Stop/Dispose 配对错位调用。</para>
/// </remarks>
internal static partial class WinMm
{
#if WINDOWS
    [System.Runtime.InteropServices.LibraryImport("winmm")]
    public static partial uint TimeBeginPeriod(uint ms);

    [System.Runtime.InteropServices.LibraryImport("winmm")]
    public static partial uint TimeEndPeriod(uint ms);
#else
    public static uint TimeBeginPeriod(uint ms) => 0;
    public static uint TimeEndPeriod(uint ms) => 0;
#endif
}
