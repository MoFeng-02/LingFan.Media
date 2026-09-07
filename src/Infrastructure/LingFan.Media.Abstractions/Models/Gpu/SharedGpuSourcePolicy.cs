using System;

namespace LingFan.Media.Abstractions;

/// <summary>
/// 共享表面源选型策略（宿主层设定，模块层只消费）：当同一环境存在多个可用的共享表面源时
/// （如 Android 上 Vulkan 源与 OpenGL 源并存），由**宿主**按其渲染后端声明优先的句柄形态，
/// 各工厂据此自报 <see cref="ISharedGpuSurfaceSourceFactory.IsAvailable"/>。
/// </summary>
/// <remarks>
/// <para><b>为什么需要</b>：源的选择发生在挂载期，而宿主渲染后端（Vulkan / OpenGL ES / 软件）
/// 只有宿主自己知道；渲染期才发现不匹配会形成「不渲染 ⇒ 不失败 ⇒ 不回退」的死锁——
/// 因此把选型决策前置到宿主，是对该死锁的结构性规避。</para>
/// <para><b>默认值</b>：<see langword="null"/> = 不干预（各工厂按平台默认自报）。</para>
/// </remarks>
public static class SharedGpuSourcePolicy
{
    // -1 = 未设置；否则为 SharedGpuHandleKind 的底层值（Volatile 需要 blittable 类型）。
    private static int _preferredKind = -1;

    /// <summary>
    /// 宿主期望优先的共享表面句柄形态；<see langword="null"/> 表示不干预（按平台默认）。
    /// </summary>
    /// <remarks>跨线程读写经 <see cref="Volatile"/>（宿主启动期写入一次，渲染/管线线程读取）。</remarks>
    public static SharedGpuHandleKind? PreferredKind
    {
        get
        {
            var v = Volatile.Read(ref _preferredKind);
            return v < 0 ? null : (SharedGpuHandleKind)v;
        }
        set => Volatile.Write(ref _preferredKind, value.HasValue ? (int)value.Value : -1);
    }
}
