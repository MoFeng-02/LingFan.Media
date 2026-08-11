namespace LingFan.Media.Abstractions;

/// <summary>
/// 共享 GPU 表面源（中立契约）。把一帧视频写入一块<b>可被宿主 UI 合成器直接导入</b>的
/// 跨设备共享 GPU 纹理，从而实现「无空域、纯控件级」的 GPU 上屏。
/// </summary>
/// <remarks>
/// <para><b>解耦目的</b>：这是「GPU 适配层 ↔ UI 渲染器层」之间唯一的桥。
/// 实现方（D3D11 / Vulkan / Metal 适配器）独占全部具体 GPU API；
/// 消费方（如 Avalonia 的 Composition 渲染器）只看到句柄与互斥键，<b>不引用任何 GPU 库</b>。
/// 新增 GPU 后端 = 新增一个实现，渲染器层零改动。</para>
/// <para><b>与 <c>IGpuPresenter</c> 的区别</b>：<c>IGpuPresenter</c> 走原生窗口 SwapChain（需 HWND，
/// 产生独立合成树 → 有空域，无法被 UI 内容遮挡/裁剪/变换）。本契约产出的是一块<b>共享纹理</b>，
/// 交由宿主合成器作为普通视觉合成，因而无空域。</para>
/// <para><b>同步模型（keyed mutex）</b>：底层纹理以 keyed mutex 保护，双方轮流持有：</para>
/// <list type="number">
/// <item>生产者（本接口实现）在 <see cref="TryWriteFrame"/> 内部取锁 → GPU 写入 → 以
/// <see cref="ConsumerAcquireKey"/> 释放；</item>
/// <item>消费者（UI 合成器）以 <see cref="ConsumerAcquireKey"/> 取锁 → 采样 → 以
/// <see cref="ConsumerReleaseKey"/> 释放，交还给生产者。</item>
/// </list>
/// <para><b>异步策略</b>：<see cref="TryWriteFrame"/> 为同步（native 分类）——GPU 命令提交是同步调用，
/// 无真实 I/O 可 await，补 async 即伪异步。</para>
/// <para><b>线程</b>：<see cref="TryWriteFrame"/> 由管线线程调用，实现须自行保证与 UI/合成线程的并发安全
/// （如共享设备开启多线程保护）。</para>
/// <para><b>帧所有权</b>：<see cref="TryWriteFrame"/> 收到的 <c>VideoFrame</c> 为<b>只读借用</b>，
/// 方法返回后即失效——实现必须在方法内同步完成读取，严禁留存引用。</para>
/// </remarks>
public interface ISharedGpuSurfaceSource : IDisposable
{
    /// <summary>本源产出的共享句柄类型。消费方据此判断宿主合成器是否支持。</summary>
    SharedGpuHandleKind HandleKind { get; }

    /// <summary>消费方<b>获取</b>表面时应使用的 keyed mutex 键。</summary>
    ulong ConsumerAcquireKey { get; }

    /// <summary>消费方<b>归还</b>表面时应使用的 keyed mutex 键。</summary>
    ulong ConsumerReleaseKey { get; }

    /// <summary>
    /// 将一帧写入共享表面（必要时按帧尺寸重建底层纹理）。
    /// </summary>
    /// <param name="frame">待写入的视频帧（只读借用，方法返回后失效）。</param>
    /// <param name="descriptor">写入成功时输出共享表面描述符。</param>
    /// <returns>
    /// <see langword="true"/>：写入完成，表面已交给消费方（可提交合成）；
    /// <see langword="false"/>：本帧被跳过（如互斥等待超时、帧格式不受支持），调用方应丢弃本帧而非重试。
    /// </returns>
    /// <remarks>返回 <see langword="false"/> 属正常降级路径，不应抛异常打断管线。</remarks>
    bool TryWriteFrame(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor);
}

/// <summary>
/// <see cref="ISharedGpuSurfaceSource"/> 的工厂（中立契约）。经 DI 注册多个实现，
/// 由消费方按「宿主合成器支持的句柄类型」挑选可用者。
/// </summary>
/// <remarks>
/// <para><b>注册即插拔</b>：每个 GPU 后端注册一个工厂；UI 层遍历
/// <c>IEnumerable&lt;ISharedGpuSurfaceSourceFactory&gt;</c>，选中第一个
/// <see cref="IsAvailable"/> 且句柄类型被宿主合成器支持的工厂。
/// 因此 UI 层不含任何「优先 D3D11 / 其次 Vulkan」的硬编码分支。</para>
/// <para><b>开箱即用原则</b>：<see cref="IsAvailable"/> 应为轻量判定（平台/DI 可用性），
/// <b>不得</b>在此触碰原生资源；真正的设备/纹理创建延迟到 <see cref="Create"/>。</para>
/// </remarks>
public interface ISharedGpuSurfaceSourceFactory
{
    /// <summary>本工厂产出的共享句柄类型（不创建实例即可查询）。</summary>
    SharedGpuHandleKind HandleKind { get; }

    /// <summary>当前环境下本适配器是否可用（平台匹配、依赖就绪）。轻量判定，不触碰原生资源。</summary>
    bool IsAvailable { get; }

    /// <summary>创建共享表面源实例。由调用方负责释放。</summary>
    /// <returns>共享表面源。</returns>
    /// <exception cref="NotSupportedException">当前环境无法创建时（调用方应回退到下一个工厂）。</exception>
    ISharedGpuSurfaceSource Create();
}
