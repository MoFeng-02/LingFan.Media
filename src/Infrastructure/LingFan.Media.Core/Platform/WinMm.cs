namespace LingFan.Media.Core.Platform;

/// <summary>
/// winmm.dll 高精度系统定时器互操作（Windows 生效，其余平台无操作）。
/// </summary>
/// <remarks>
/// <para><see cref="TimeBeginPeriod"/> 将系统定时器分辨率提高到 <paramref name="ms"/> 毫秒，
/// 使 <see cref="System.Threading.Thread.Sleep"/> 的粒度从默认 15.6ms 降到 ~1ms ——
/// 这是视频帧精确等待（frame pacing）消抖的必要前提：否则 <c>Thread.Sleep(1)</c> 实际睡 1~15.6ms，
/// 本身即 ±15ms 量级的抖动源。</para>
/// <para><b>必须对称调用 <see cref="TimeEndPeriod"/> 还原</b>，否则整机定时器维持高精度（影响功耗）。
/// 由 <see cref="MediaPlayer"/> 在 Play/Stop/Dispose 配对调用。</para>
/// <para><b>平台分派</b>：经 <see cref="OperatingSystem.IsWindows"/> 运行时探测（共享层零编译期平台宏）；
/// 非 Windows 平台返回 0（无操作），不加载 winmm。原生导出名 <c>timeBeginPeriod</c>/<c>timeEndPeriod</c>
/// 为小写，而 <see cref="System.Runtime.InteropServices.LibraryImportAttribute"/> 默认 ExactSpelling
/// 不做大小写探测，故经 EntryPoint 显式指定小写导出名。</para>
/// </remarks>
internal static partial class WinMm
{
    /// <summary>提升系统定时器分辨率到 <paramref name="ms"/> 毫秒；非 Windows 返回 0（无操作）。</summary>
    public static uint TimeBeginPeriod(uint ms)
        => OperatingSystem.IsWindows() ? TimeBeginPeriodCore(ms) : 0;

    /// <summary>还原系统定时器分辨率；非 Windows 返回 0（无操作）。须与 <see cref="TimeBeginPeriod"/> 配对。</summary>
    public static uint TimeEndPeriod(uint ms)
        => OperatingSystem.IsWindows() ? TimeEndPeriodCore(ms) : 0;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.LibraryImport("winmm", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriodCore(uint ms);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.LibraryImport("winmm", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriodCore(uint ms);
}
