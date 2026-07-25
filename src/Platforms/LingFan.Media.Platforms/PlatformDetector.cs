namespace LingFan.Media.Platforms;

/// <summary>
/// 运行时平台检测器。
/// </summary>
/// <remarks>
/// <para>使用 <see cref="OperatingSystem"/> 静态方法检测当前运行平台，
/// 供后端、渲染器、输出等模块按平台选择实现。</para>
/// <para><b>异步策略</b>：全部同步（config 分类）——<see cref="OperatingSystem"/> 静态方法是纯内存查询，
/// 无 I/O，无需异步化。若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：static 类，编译期确定所有分支，无反射。</para>
/// </remarks>
public static class PlatformDetector
{
    /// <summary>
    /// 检测当前运行平台。
    /// </summary>
    /// <returns>当前平台的 <see cref="OSPlatform"/> 值。</returns>
    public static OSPlatform Detect()
    {
        if (OperatingSystem.IsWindows()) return OSPlatform.Windows;
        if (OperatingSystem.IsLinux()) return OSPlatform.Linux;
        if (OperatingSystem.IsMacOS()) return OSPlatform.OSX;
        if (OperatingSystem.IsAndroid()) return OSPlatform.Create("Android");
        if (OperatingSystem.IsIOS()) return OSPlatform.Create("iOS");
        return OSPlatform.Create("Unknown");
    }

    /// <summary>当前是否为 Windows 平台。</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>当前是否为 Linux 平台。</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>当前是否为 macOS 平台。</summary>
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>当前是否为 Android 平台。</summary>
    public static bool IsAndroid => OperatingSystem.IsAndroid();

    /// <summary>当前是否为 iOS 平台。</summary>
    public static bool IsIOS => OperatingSystem.IsIOS();
}
