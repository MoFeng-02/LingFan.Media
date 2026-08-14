namespace LingFan.Media.Renderers.OpenGLES.Context;

/// <summary>
/// 跨平台 OpenGL ES 上下文抽象（Android EGL）。
/// </summary>
/// <remarks>
/// <para>渲染器不直接依赖具体平台绑定：<see cref="OpenGLESRenderer"/> 经此接口统一 MakeCurrent / SwapBuffers / 释放，
/// 平台分支由 <see cref="OpenGLESRenderer.Attach"/> 按 <see cref="OperatingSystem"/> 守卫创建（当前仅 Android）。</para>
/// <para>本接口仅描述<b>上屏</b> GLES 上下文：在 <see cref="OpenGLESRenderer.Attach"/> 时按窗口建立、<see cref="OpenGLESRenderer.Detach"/> / <see cref="Dispose"/> 时释放，为渲染器实例级。
/// 与桌面 GL（<see cref="LingFan.Media.Renderers.OpenGL"/>）不同，Android GLES 当前无工厂级离屏设备上下文（零拷贝属 C 线未来增强）。</para>
/// </remarks>
internal interface IGlContext : IDisposable
{
    /// <summary>将上下文绑定到调用线程（成为当前 GLES 上下文）。</summary>
    void MakeCurrent();

    /// <summary>解绑调用线程上的当前 GLES 上下文（交还上下文，供其他线程经 <see cref="MakeCurrent"/> 重新绑定）。</summary>
    /// <remarks>GLES 上下文具线程亲和性，同一时刻仅能在一个线程 current。渲染器每次 GLES 操作完毕后调用本方法，
    /// 使 <see cref="OpenGLESRenderer.Present"/>（管线线程）与 <see cref="OpenGLESRenderer.Detach"/> / <see cref="Dispose"/>（主线程）可安全交替绑定。</remarks>
    void ReleaseCurrent();

    /// <summary>交换前后缓冲，呈现已渲染内容。</summary>
    void SwapBuffers();

    /// <summary>GLES 主版本号（如 3）。</summary>
    int GlMajor { get; }

    /// <summary>GLES 次版本号（如 0）。</summary>
    int GlMinor { get; }

    /// <summary>GLES 上下文句柄（EGLContext）。</summary>
    nint ContextHandle { get; }

    /// <summary>平台显示/设备上下文句柄（EGLDisplay）。</summary>
    nint PlatformDisplay { get; }
}
