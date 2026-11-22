using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LingFan.Media.Backends.VLCNative.Demuxer;

// VLCNativeDemuxer 的 AudioRelease 职责段（部分类，由 VLCNativeDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Close/Dispose）在主文件；VLC 回调式帧交付适配为拉取式接口。
internal sealed partial class VLCNativeDemuxer
{
    private void EnsureAudioReleaseStarted()
    {
        if (_audioReleaseTask is { IsCompleted: false }) return;
        lock (_audioReleaseLock)
        {
            if (_audioReleaseTask is { IsCompleted: false }) return;
            RestartAudioRelease();
        }
    }

    private void RestartAudioRelease()
    {
        try { _audioReleaseCts?.Cancel(); } catch { }
        try { _audioReleaseCts?.Dispose(); } catch { }
        _audioReleaseCts = null;
        _audioReleaseTask = null;
        while (_audioJitter.TryDequeue(out var p)) p.Dispose();

        _audioReleaseCts = new CancellationTokenSource();
        var ct = _audioReleaseCts.Token;
        _audioReleaseTask = Task.Run(() => AudioReleaseLoop(ct), ct);
    }

    private async Task AudioReleaseLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_audioJitter.TryDequeue(out var pkt))
                {
                    // 无条件写入独立音频通道：该通道仅承载音频，DropOldest 仅丢最旧音频（偶发微疵），
                    // 视频突发绝不再能挤掉音频；抖动缓冲继续在生产者侧吸收后续音频。
                    await _audioChannel.Writer.WriteAsync(pkt, ct);
                }
                else
                {
                    await Task.Delay(2, ct); // 缓冲暂空：短暂让出，等待生产者
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_audioJitter.TryDequeue(out var leftover)) leftover.Dispose();
        }
    }
}
