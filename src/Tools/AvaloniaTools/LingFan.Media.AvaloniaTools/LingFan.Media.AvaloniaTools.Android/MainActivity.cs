using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using LingFan.Media.Backends.MediaCodec;
using LingFan.Media.Extensions;

namespace LingFan.Media.AvaloniaTools.Android;

[Activity(
    Label = "LingFan.Media.AvaloniaTools.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {

        base.OnCreate(savedInstanceState);
    }
}