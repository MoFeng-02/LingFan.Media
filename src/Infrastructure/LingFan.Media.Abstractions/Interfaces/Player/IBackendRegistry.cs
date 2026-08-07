namespace LingFan.Media.Abstractions;

/// <summary>
/// 只读后端注册表：聚合所有已注册的后端组（按 DI 注册顺序 = 回退优先级）。
/// </summary>
/// <remarks>
/// <para>仅暴露工厂接口（<see cref="BackendDescriptor"/>），不暴露具体后端实现，保持依赖倒置。</para>
/// <para>中间件（如 <c>LingFan.Media.Playback</c> 的回退调度器）实现此接口，并据此做运行时单次判断回退。</para>
/// </remarks>
public interface IBackendRegistry
{
    /// <summary>所有已注册的后端组，按 DI 注册顺序（即回退优先级，先注册先试）。</summary>
    IReadOnlyList<BackendDescriptor> Backends { get; }
}
