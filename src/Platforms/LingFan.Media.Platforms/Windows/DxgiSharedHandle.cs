namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// DXGI 共享句柄管理。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：管理 D3D11 资源的 DXGI 共享句柄，用于跨进程 / 跨 API 资源共享。
/// 通过 <c>IDXGIResource::GetSharedHandle</c> 和 <c>ID3D11Device1::OpenSharedResource1</c> 实现。</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// V1 D3D11Renderer 直接持有 ID3D11Device，无需跨进程共享。
/// 未来 DirectComposition 集成或多进程渲染场景需要共享句柄。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——COM 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class DxgiSharedHandle
{
    /// <summary>
    /// 从 D3D11 资源创建 DXGI 共享句柄。
    /// </summary>
    /// <param name="resource">ID3D11Resource（Texture2D / Buffer）COM 句柄。</param>
    /// <returns>DXGI 共享句柄（HANDLE）。</returns>
    public nint CreateSharedHandle(nint resource)
        => throw new NotSupportedException("DXGI 共享句柄管理尚未实现。");

    /// <summary>
    /// 从 DXGI 共享句柄打开 D3D11 资源。
    /// </summary>
    /// <param name="handle">DXGI 共享句柄。</param>
    /// <returns>ID3D11Texture2D COM 句柄。</returns>
    public nint OpenSharedResource(nint handle)
        => throw new NotSupportedException("DXGI 共享句柄管理尚未实现。");

    /// <summary>
    /// 使用 Keyed Mutex 同步跨 API 资源访问。
    /// </summary>
    /// <param name="handle">DXGI 共享句柄。</param>
    /// <param name="key">同步键值。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <returns>是否成功获取同步锁。</returns>
    public bool AcquireSync(nint handle, ulong key, int timeoutMs)
        => throw new NotSupportedException("DXGI KeyedMutex 同步尚未实现。");

    /// <summary>
    /// 释放 Keyed Mutex 同步锁。
    /// </summary>
    /// <param name="handle">DXGI 共享句柄。</param>
    /// <param name="key">同步键值。</param>
    public void ReleaseSync(nint handle, ulong key)
        => throw new NotSupportedException("DXGI KeyedMutex 同步尚未实现。");
}
