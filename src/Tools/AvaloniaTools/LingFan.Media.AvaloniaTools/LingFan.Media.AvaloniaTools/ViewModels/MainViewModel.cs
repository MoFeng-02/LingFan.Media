using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingFan.Media.Abstractions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LingFan.Media.AvaloniaTools.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _sp;

    /// <summary>当前播放器，绑定到 VideoView.Player（先 OpenAsync 再赋值，符合 VideoView 约定）。</summary>
    [ObservableProperty]
    private IMediaPlayer? _player;

    [ObservableProperty]
    private string _status = "请点击「打开文件」选择一个媒体文件";

    public MainViewModel(IServiceProvider sp)
    {
        _sp = sp;
    }

    /// <summary>
    /// 打开并播放指定文件。由 MainView 的文件选择器 Click 处理器传入本地路径。
    /// 内部：解析回退工厂 → 创建播放器 → 先 OpenAsync（Session 就绪）→ 再绑定 Player → PlayAsync。
    /// 三后端（FFmpeg/VLC/MF）由回退工厂按注册顺序自动选可用者。
    /// </summary>
    [RelayCommand]
    private async Task OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Status = $"正在打开：{Path.GetFileName(path)} …";

            // 释放上一个播放器（先解绑再 Dispose，避免 VideoView 仍引用旧帧通道）
            if (Player is not null)
            {
                var old = Player;
                Player = null;
                await old.DisposeAsync();
            }

            var factory = _sp.GetRequiredService<IMediaPlayerFactory>();
            var player = factory.Create();

            // 先 Open（Session 就绪），再绑定 VideoView.Player —— 遵守 VideoView 的绑定契约
            await player.OpenAsync(new FileMediaSource(path), CancellationToken.None);

            Player = player;          // 绑定到 VideoView → 触发帧通道订阅 / GPU Presenter 接管
            await player.PlayAsync(); // A/V 编排（视频首帧上屏后再起音频）由播放器内部完成

            Status = $"播放中：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Status = $"打开失败：{ex.Message}";
        }
    }

    /// <summary>播放 / 暂停切换。</summary>
    [RelayCommand]
    private async Task TogglePlay()
    {
        if (Player is null)
            return;

        try
        {
            if (Player.State == MediaState.Playing)
                await Player.PauseAsync();
            else
                await Player.PlayAsync();
        }
        catch (Exception ex)
        {
            Status = $"操作失败：{ex.Message}";
        }
    }
}
