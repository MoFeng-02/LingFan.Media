using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LingFan.Media.Extensions;

/// <summary>
/// 为 Microsoft.Extensions.DependencyInjection 补充 <see cref="Lazy{T}"/> 自动解析支持。
/// </summary>
/// <remarks>
/// <para><b>背景</b>：默认 MS DI 仅自动解析 <see cref="System.Collections.Generic.IEnumerable{T}"/> /
/// <see cref="System.Collections.Generic.IList{T}"/> / 数组等集合类型，<b>不</b>自动解析 <see cref="Lazy{T}"/> 或
/// <see cref="Func{TResult}"/>。若直接注入 <c>Lazy&lt;T&gt;</c> 会抛 "Unable to resolve service for type 'System.Lazy`1[...]'"。</para>
/// <para><b>用途</b>：注册后，<c>Lazy&lt;T&gt;</c> 仅在首次访问 <see cref="Lazy{T}.Value"/> 时才从容器解析 T，
/// 即把构造延迟到真正使用时。本库用此机制把后端原生初始化（MFBackend→MFStartup / VLCBackend→new LibVLC）
/// 严格延迟到 Session 创建（Open），满足「注册一个后端 ≠ 马上要它的 native 库」的约定。</para>
/// </remarks>
public static class LazySupport
{
    /// <summary>注册 <see cref="Lazy{T}"/> 的通用（open generic）解析。应在 <c>AddLingFanMedia</c> 早期调用一次；幂等（TryAdd）。</summary>
    public static IServiceCollection AddLazySupport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(typeof(Lazy<>), typeof(LazyResolver<>));
        return services;
    }
}

/// <summary>通用 <see cref="Lazy{T}"/> 解析桥：构造时捕获根 <see cref="IServiceProvider"/>，首次 <see cref="Lazy{T}.Value"/> 才解析 T。</summary>
/// <remarks>
/// 注册为 Singleton：捕获的 sp 为根 provider，<c>.Value</c> 解析的 T 沿用其自身注册生命周期（后端均为 Singleton），无 scope 泄漏。
/// <see cref="Lazy{T}"/> 的 <c>T</c> 在 AOT 下要求 notnull 与 PublicParameterlessConstructor 标注（与 <see cref="Lazy{T}"/> 的声明对齐）。
/// </remarks>
internal sealed class LazyResolver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T> : Lazy<T>
    where T : notnull
{
    public LazyResolver(IServiceProvider serviceProvider)
        : base(() => serviceProvider.GetRequiredService<T>())
    {
    }
}
