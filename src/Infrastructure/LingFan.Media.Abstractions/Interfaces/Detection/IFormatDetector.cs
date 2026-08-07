using System.Threading;
using System.Threading.Tasks;

namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体格式探测器契约（依赖倒置点）。
/// </summary>
/// <remarks>
/// <para>在 Open 前对可定位流做轻量头部探测，返回 <see cref="MediaFormatProfile"/>(容器, 主视频编码)，
/// 供回退调度器提前命中「格式级记忆」、跳过已知坏后端，避免每次同格式重新走异常驱动回退。</para>
/// <para>具体实现位于基础设施层（LingFan.Media.Formats 的 <c>FormatDetector</c>）。
/// 高层中间件（LingFan.Media.Playback）仅依赖本契约，<b>不</b>引用具体实现，严守依赖倒置（DIP）。</para>
/// <para>仅读流头部魔数/编码标识，不建立完整 Session；不可定位流返回 Unknown/Unknown。</para>
/// </remarks>
public interface IFormatDetector
{
    /// <summary>同步轻量探测 (容器, 视频编码)。不可定位流返回 Unknown/Unknown。</summary>
    /// <param name="stream">媒体数据流（应可定位）。</param>
    /// <returns>探测到的 <see cref="MediaFormatProfile"/>。</returns>
    MediaFormatProfile DetectProfile(IMediaStream stream);

    /// <summary>异步轻量探测 (容器, 视频编码)。不可定位流返回 Unknown/Unknown。</summary>
    /// <param name="stream">媒体数据流（应可定位）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>探测到的 <see cref="MediaFormatProfile"/>。</returns>
    Task<MediaFormatProfile> DetectProfileAsync(IMediaStream stream, CancellationToken ct = default);
}
