namespace LingFan.Media.Abstractions;

/// <summary>
/// 渲染器运行期健康通知契约。
/// </summary>
/// <remarks>
/// <para><b>用途</b>：渲染器可能在 <see cref="IVideoRenderer.Attach"/> 成功、但运行期持续无法出画
/// （如 Composition 合成器导入跨设备纹理失败）。此时若仍静默空白且不回退，违反「不能让某渲染器玩不了」
/// 的公平性要求。实现本接口的渲染器在持续失败时触发 <see cref="Unhealthy"/>，宿主据此把该渲染器
/// 拉黑并重建回退链（如 Composition → Skia），保证总有可用路径出画。</para>
/// <para><b>解耦</b>：定义在 Abstractions，UI 层（VideoView）只按契约订阅，不引用具体渲染器类型。</para>
/// <para><b>触发口径</b>：本事件是<b>单次</b>信号——同一实例至多触发一次，实现须用原子闸门防止
/// 管线线程与 UI 线程并发重复触发。「连续失败」的阈值由实现自定（当前实现取 3～10 次连续呈现/绘制失败），
/// 契约只要求判据是<b>连续</b>失败而非累计失败、且阈值 ≥ 1：突发一次失败不应直接拉黑渲染器。</para>
/// <para><b>无恢复通道</b>：本契约不提供「恢复健康」的通知。宿主收到后应把该实例与其工厂类型一起
/// 移出候选链并回退到下一个渲染器；同一实例不会经本契约回到可用状态，也不得被重新启用。</para>
/// <para><b>AOT 兼容</b>：仅声明事件，无反射。</para>
/// </remarks>
public interface IRendererHealth
{
    /// <summary>
    /// 渲染器运行期不健康（连续无法呈现）时触发一次。
    /// 由渲染器在管线线程触发；宿主须切回 UI 线程处理（重建回退链），避免跨线程操作控件。
    /// </summary>
    event Action? Unhealthy;
}
