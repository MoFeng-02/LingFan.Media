namespace LingFan.Media.Core;

/// <summary>
/// 管线释放超时常量（Core 侧）。
/// </summary>
/// <remarks>
/// <para>设计决策：超时采用固定内部常量，不暴露到 <c>MediaPlayerOptions</c>；
/// 可配置化留待音视频管道重构统一处理，避免多一次契约面变更。</para>
/// <para>注意：MF 后端的「原生 drain / 调度器 Join」超时是另一组常量，位于
/// <c>LingFan.Media.Backends.MediaFoundation.Concurrency.MediaPipelineTimeouts</c>；
/// 本类只覆盖 Core 侧管线线程 / Task 的等待，二者互不依赖（跨程序集、均为 internal）。</para>
/// <para>安全性说明：这些超时**不是**安全依赖。即便全部超时，原生指针也不会被 use-after-free 释放——
/// 真正的安全边界由后端 <c>NativeCallGate</c> 两阶段关闭协议保证（超时即有意泄漏，不释放）。
/// 本类超时只影响「泄漏概率」与「关闭延迟」，不影响进程存活。</para>
/// </remarks>
internal static class MediaPipelineTimeouts
{
    /// <summary>等待管线线程退出（DisposeAsync 步骤 1 <c>Step_StopPipelinesAsync</c>）的超时。</summary>
    public static readonly TimeSpan PipelineJoin = TimeSpan.FromSeconds(5);

    /// <summary>同步 <c>Dispose</c> / <c>CleanupAsync</c> 等待管线 Task（含 BufferManager.ReaderTask）退出的超时。</summary>
    public static readonly TimeSpan PipelineTaskWait = TimeSpan.FromSeconds(2);
}
