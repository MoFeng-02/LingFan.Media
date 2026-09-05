using System.Collections.Concurrent;
using System.Collections.Generic;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 共享表面源工厂的进程级选择记忆。
/// </summary>
/// <remarks>
/// <para>首次成功选定某个 <see cref="ISharedGpuSurfaceSourceFactory"/> 后，将其按合成器上下文键缓存；
/// 后续 <see cref="CompositionVideoRenderer.Attach"/> 直接优先命中缓存，不再每次从注册序头部逐个探测
/// （Vulkan→D3D11→…→软渲），消除"回退一个一个试"的重复开销。</para>
/// <para>对标后端 demuxer 工厂持 <c>Lazy&lt;*Backend&gt;</c> 的"解析一次、记忆复用"模式：昂贵的后端
/// 解析只发生一次，之后直接复用已验证结果。</para>
/// <para>缓存项在尝试时失败会被 <see cref="Invalidate"/> 清除，强制下次全扫描，避免"记住了坏结果"；
/// 键由合成器支持的句柄类型集合 + 合成器所在 GPU 身份（UUID/LUID）构成，跨 GPU/远程桌面合成器切换
/// 时自然重新探测。</para>
/// <para>AOT 兼容：sealed、无反射、仅用 <see cref="ConcurrentDictionary{TKey,TValue}"/>。</para>
/// </remarks>
public sealed class SharedGpuSurfaceSourceSelector
{
    private readonly ConcurrentDictionary<string, ISharedGpuSurfaceSourceFactory> _cache = new();

    /// <summary>尝试按上下文键取出已缓存的胜出厂；要求它仍在当前注入的工厂集合中（实例一致）。</summary>
    /// <returns>命中且实例仍有效返回 <see langword="true"/>，<paramref name="cached"/> 为缓存工厂；否则 <see langword="false"/>。</returns>
    public bool TryGet(string key, IEnumerable<ISharedGpuSurfaceSourceFactory> factories, out ISharedGpuSurfaceSourceFactory? cached)
    {
        if (_cache.TryGetValue(key, out var f) && f is not null)
        {
            // 实例一致性校验：DI 重建（极端情况）后旧引用失效即视为未命中。
            foreach (var x in factories)
            {
                if (ReferenceEquals(x, f))
                {
                    cached = f;
                    return true;
                }
            }
        }

        cached = null;
        return false;
    }

    /// <summary>记录某上下文下的胜出厂。</summary>
    public void Record(string key, ISharedGpuSurfaceSourceFactory factory) => _cache[key] = factory;

    /// <summary>使某上下文的缓存失效（下次全扫描）。</summary>
    public void Invalidate(string key) => _cache.TryRemove(key, out _);
}
