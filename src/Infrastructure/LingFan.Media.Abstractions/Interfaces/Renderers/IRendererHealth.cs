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
