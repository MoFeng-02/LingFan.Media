using Avalonia;
using Avalonia.iOS;

namespace LingFan.Media.AvaloniaTools.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
/// <summary>iOS 应用委托：启动 Avalonia 应用界面并响应系统生命周期事件。</summary>
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    /// <summary>定制应用构建器（追加 Inter 字体）。</summary>
    /// <param name="builder">基类预配置的应用构建器。</param>
    /// <returns>定制后的构建器。</returns>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
