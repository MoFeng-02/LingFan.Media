namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧池感知接口。可选实现，支持帧对象池的组件通过此接口接收池引用。
/// </summary>
/// <typeparam name="T">帧类型。</typeparam>
/// <remarks>
/// <para>解码器等组件可选实现此接口。调用方（如 MediaPlayer）通过 pattern matching 检测并注入池。</para>
/// <para>不修改现有接口（IMediaComponent/IVideoDecoder 等），通过 pattern matching 注入，保持架构兼容。</para>
/// <para><b>AOT 兼容</b>：接口 + pattern matching（is 运算符），编译期确定类型，无反射。</para>
/// <para><b>异步策略</b>：sync（config 分类）——纯内存设置引用，无 I/O。</para>
/// </remarks>
public interface IFramePoolAware<T> where T : class
{
    /// <summary>
    /// 设置帧对象池。传入 null 表示禁用池化，组件将自行创建帧实例。
    /// </summary>
    /// <param name="pool">帧对象池（可为 null）。</param>
    void SetFramePool(IFramePool<T>? pool);
}
