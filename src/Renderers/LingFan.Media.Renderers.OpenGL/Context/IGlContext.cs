namespace LingFan.Media.Renderers.OpenGL.Context;

/// <summary>
/// 跨平台 OpenGL 上下文抽象（Windows WGL / Linux EGL）。
/// </summary>
/// <remarks>
/// <para>渲染器不直接依赖具体平台绑定：<see cref="OpenGLRenderer"/> 经此接口统一 MakeCurrent / SwapBuffers / 释放，
/// 平台分支由 <see cref="OpenGLRenderer.Attach"/> 按 <see cref="OperatingSystem"/> 守卫创建（Windows→WglContext / Linux→EglContext）。</para>
/// <para>上下文在 <see cref="OpenGLRenderer.Attach"/> 时建立、<see cref="OpenGLRenderer.Detach"/> / <see cref="Dispose"/> 时释放；
/// 与 D3D11/Vulkan 不同，GL 上下文为渲染器实例级（非工厂共享 Device 单例），工厂保持薄。</para>
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
}
