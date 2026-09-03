using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Controls.ApplicationLifetimes;
using LingFan.Media.Backends.MediaCodec;
using LingFan.Media.Extensions;

// R2 探针（2026-09-01，验证通过后随探针一并撤销）：验证 Android 真机上 IgnoresAccessChecksTo 是否生效，
// 以及能否从公开的 Compositor.TryGetCompositionGpuInterop() 反推到 Avalonia 的 IVulkanDevice
// （VkDevice 句柄 / VkPhysicalDevice / 主队列 / 队列族）。可行 ⇒ 实现「同 device 建视频纹理 + 渲染流程内直绘」。
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Avalonia.Vulkan")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Avalonia.Base")]

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

    protected override async void OnResume()
    {
        base.OnResume();
        await RunGpuProbeAsync();
    }

    private async Task RunGpuProbeAsync()
    {
        try
        {
            // 等 Avalonia 合成器与 Vulkan 后端就绪（首帧/首布局之后 GPU interop 才可用）。
            await Task.Delay(2000);

            if (global::Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime single)
            {
                Console.WriteLine("[R2PROBE][WARN] [0] 生命周期不是 ISingleViewApplicationLifetime。");
                return;
            }
            var top = TopLevel.GetTopLevel(single.MainView);
            if (top is null)
            {
                Console.WriteLine("[R2PROBE][WARN] [0] 未取到 TopLevel。");
                return;
            }

            // TopLevel.Compositor 非公开；经公开的 ElementComposition 反查所属 Compositor。
            var visual = ElementComposition.GetElementVisual(top);
            if (visual is null)
            {
                Console.WriteLine("[R2PROBE][WARN] [1] 未取到 TopLevel 的 CompositionVisual。");
                return;
            }
            var compositor = visual.Compositor;
            var interop = await compositor.TryGetCompositionGpuInterop();
            Console.WriteLine($"[R2PROBE] [1] interop={interop?.GetType().FullName ?? "null"}");

            // [2/3] 注入生效性改用行为判据：若 Avalonia 采用了我们的 device，
            // 其初始化/渲染过程中必然调用 IVulkanDevice.Lock()（适配器内有一次性日志）。
            // AvaloniaLocator.Current 是 private（IgnoresAccessChecksTo 只放宽 internal），读不到，已绕过。

            // [4] interop 实现类型（Skia 侧 internal 不可穿透，先记录实际类型名供下轮决策）
            Console.WriteLine($"[R2PROBE] [4] interop 实际类型={interop?.GetType().AssemblyQualifiedName ?? "null"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R2PROBE][ERR] 探针异常: {ex}");
        }
    }
}
