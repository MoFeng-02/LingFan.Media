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
    /// 打开并播放指定文件（工厂式并行会话）。
    /// 设计依据：Session 隔离——每个 IMediaPlayer 拥有独立 Session（Clock/Buffer/Pipeline），
    /// 多实例并行是架构支持的目标形态。覆盖式（先销毁旧再建新）会把旧会话的满负荷设备侧
    /// 释放（解码器实例/gralloc/GPU）压进新会话最脆弱的启动窗口。
    /// 顺序：创建新播放器 → OpenAsync（旧会话不受影响继续播放）→ 绑定切换 → PlayAsync → 旧会话后台释放。
    /// </summary>
    [RelayCommand]
    private async Task OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var factory = _sp.GetRequiredService<IMediaPlayerFactory>();
        var player = factory.Create();
        var old = Player;

        try
        {
            Status = $"正在打开：{Path.GetFileName(path)} …";
            // 新会话独立 Open（Session 隔离：旧会话此刻仍在正常播放，互不干扰）
            await player.OpenAsync(new FileMediaSource(path), CancellationToken.None);
            Player = player;          // 绑定 VideoView（帧通道/呈现器切换到新会话）
            await player.PlayAsync();
            Status = $"播放中：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            // 新会话失败：丢弃新播放器，旧会话照常播放（不因切换失败中断当前播放）
            try { await player.DisposeAsync(); } catch { }
            Status = $"打开失败：{ex.Message}";
            return;
        }

        // 旧会话后台优雅释放：不阻塞 UI 与新会话启动，设备侧释放在新会话稳定后进行
        if (old is not null)
            _ = DisposeOldAsync(old);
    }

    private static async Task DisposeOldAsync(IMediaPlayer old)
    {
        try { await old.DisposeAsync(); }
        catch { /* 旧会话释放失败不影响新会话 */ }
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
