using UIKit;

namespace LingFan.Media.AvaloniaTools.iOS;

/// <summary>iOS 进程入口：把控制权交给 UIKit 并指定应用委托。</summary>
public class Application
{
    // This is the main entry point of the application.
    static void Main(string[] args)
    {
        // if you want to use a different Application Delegate class from "AppDelegate"
        // you can specify it here.
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
