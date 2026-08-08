using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LingFan.Media.Avalonia;
using LingFan.Media.AvaloniaTools.ViewModels;
using System;

namespace LingFan.Media.AvaloniaTools.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // VideoView 在 Attach 到视觉树时按已注册的 IVideoRendererFactory 集合解析渲染器，
        // 并自动回退（与后端回退同构）：GPU 原生渲染器在 Avalonia 控件内因需 Pointer/HWND 而失败，
        // 自动落到内置 SkiaVideoRenderer 软渲染（解码仍走 GPU）。须先注入 Services 供工厂解析。
        if (this.FindControl<VideoView>("VideoView") is { } videoView)
        {
            videoView.Services = App.Services;
        }
    }

    /// <summary>
    /// 打开文件按钮：经 TopLevel.StorageProvider 调起跨平台文件选择器（Windows 原生 / Android 系统选择器）。
    /// 选中后将路径交给 ViewModel 的 OpenFileCommand 完成打开与播放。
    /// </summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择媒体文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("视频文件")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi" }
                },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0 && DataContext is MainViewModel vm)
        {
            // LocalPath 在桌面平台即文件系统路径；移动平台为 content URI，后续按需改为流方式打开。
            vm.OpenFileCommand.Execute(files[0].Path.LocalPath);
        }
    }
}
