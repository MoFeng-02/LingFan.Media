using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingFan.Media.Abstractions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
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

    // 旧会话释放队列（单消费者 FIFO）：多次强切时多个旧会话的设备侧释放（解码器实例/gralloc/
    // GPU 上下文）经此串行化，避免并发释放风暴互相挤占并波及新会话；批间让步间隔给设备侧回收留窗口。
    private readonly Channel<IMediaPlayer> _disposalQueue =
        Channel.CreateUnbounded<IMediaPlayer>(new UnboundedChannelOptions { SingleReader = true });
    private int _disposalWorkerActive;      // 0=worker 未运行 1=运行中（Interlocked 抢占防重复拉起）
    private const int DisposalGraceMs = 2000;  // 批首宽限：新会话启动最脆弱窗口内不启动销毁
    private const int DisposalBatchPaceMs = 800; // 批内节奏：连续多个旧会话逐个错峰释放

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

        // 旧会话入释放队列：单消费者按批错峰释放（新会话启动窗口与设备回收不受挤占）
        if (old is not null)
            EnqueueDisposal(old);
    }

    /// <summary>旧会话入队并按需拉起单消费者 worker（Interlocked 抢占防重复）。</summary>
    private void EnqueueDisposal(IMediaPlayer old)
    {
        _disposalQueue.Writer.TryWrite(old);
        if (Interlocked.Exchange(ref _disposalWorkerActive, 1) == 0)
            _ = DisposalWorkerAsync();
    }

    /// <summary>
    /// 释放 worker（VM 生命周期内常驻）：FIFO 按批错峰释放旧会话。
    /// 节奏：距上次释放超过 3s（空闲批）→ 批首宽限 {DisposalGraceMs}ms（避开新会话启动窗）；
    /// 连续释放 → 批间让步 {DisposalBatchPaceMs}ms（设备侧回收窗口）。单个失败不中断队列。
    /// </summary>
    private async Task DisposalWorkerAsync()
    {
        long lastDisposeDoneQpc = 0;
        try
        {
            await foreach (var old in _disposalQueue.Reader.ReadAllAsync())
            {
                var now = Stopwatch.GetTimestamp();
                bool idleBatch = lastDisposeDoneQpc == 0 ||
                    Stopwatch.GetElapsedTime(lastDisposeDoneQpc, now).TotalMilliseconds > 3000;
                await Task.Delay(idleBatch ? DisposalGraceMs : DisposalBatchPaceMs);
                try
                {
                    await old.DisposeAsync();
                }
                catch { /* 单个旧会话释放失败不影响队列后续 */ }
                lastDisposeDoneQpc = Stopwatch.GetTimestamp();
            }
        }
        catch (ChannelClosedException)
        {
            // 队列关闭：退出（本示例不关闭队列，防御性兜底）
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
