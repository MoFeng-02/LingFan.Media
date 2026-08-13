namespace LingFan.Media.Renderers.OpenGL.Context;

/// <summary>
/// 跨平台 OpenGL 上下文抽象（Windows WGL / Linux EGL）。
/// </summary>
/// <remarks>
/// <para>渲染器不直接依赖具体平台绑定：<see cref="OpenGLRenderer"/> 经此接口统一 MakeCurrent / SwapBuffers / 释放，
/// 平台分支由 <see cref="OpenGLRenderer.Attach"/> 按 <see cref="OperatingSystem"/> 守卫创建（Windows→WglContext / Linux→EglContext）。</para>
/// <para>本接口仅描述<b>上屏</b> GL 上下文：在 <see cref="OpenGLRenderer.Attach"/> 时按窗口建立、<see cref="OpenGLRenderer.Detach"/> / <see cref="Dispose"/> 时释放，为渲染器实例级。
/// 与 D3D11/Vulkan 不同，上屏 GL 上下文非工厂共享 Device 单例；但工厂额外持有<b>独立的离屏 GL 上下文单例</b>（<see cref="OpenGLOffscreenDeviceContext"/>，共享组所有者），
/// 仅作 decode-init 阶段的中立设备句柄来源与零拷贝共享组根，不参与上屏绘制，故上屏渲染路径仍保持实例级、工厂上屏侧保持薄。</para>
/// </remarks>
internal interface IGlContext : IDisposable
{
    /// <summary>将上下文绑定到调用线程（成为当前 GL 上下文）。</summary>
    void MakeCurrent();

    /// <summary>解绑调用线程上的当前 GL 上下文（交还上下文，供其他线程经 <see cref="MakeCurrent"/> 重新绑定）。</summary>
    /// <remarks>GL 上下文具线程亲和性，同一时刻仅能在一个线程 current。渲染器每次 GL 操作完毕后调用本方法，
    /// 使 <see cref="Present"/>（管线线程）与 <see cref="Detach"/> / <see cref="Dispose"/>（主线程）可安全交替绑定。</remarks>
    void ReleaseCurrent();

    /// <summary>交换前后缓冲，呈现已渲染内容。</summary>
    void SwapBuffers();

    /// <summary>GL 主版本号（如 3）。</summary>
    int GlMajor { get; }

    /// <summary>GL 次版本号（如 3）。</summary>
    int GlMinor { get; }

    /// <summary>GL 上下文句柄（HGLRC / EGLContext）。作为 <see cref="IGpuDeviceContext"/> 的 DeviceHandle / 共享组句柄。</summary>
    nint ContextHandle { get; }

    /// <summary>平台显示/设备上下文句柄（HDC / EGLDisplay）。作为 <see cref="IGpuDeviceContext"/> 的 ContextHandle（解码侧 interop 用）。</summary>
    nint PlatformDisplay { get; }
}
