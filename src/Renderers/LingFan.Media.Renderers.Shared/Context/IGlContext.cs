namespace LingFan.Media.Renderers.Shared.Context;

/// <summary>
/// 上屏 GL/GLES 上下文抽象（桌面 OpenGL：Windows WGL / Linux EGL；OpenGL ES：Android EGL）。
/// </summary>
/// <remarks>
/// <para>渲染器不直接依赖具体平台绑定：OpenGLRenderer / OpenGLESRenderer 经此接口统一
/// MakeCurrent / SwapBuffers / 释放，平台分支由各自 Attach 按 <see cref="OperatingSystem"/>
/// 守卫创建（桌面 Windows→WglContext / Linux→EglContext；GLES 当前仅 Android→AndroidEglContext）。</para>
/// <para>本接口仅描述<b>上屏</b>上下文：Attach 时按窗口建立、Detach / <see cref="IDisposable.Dispose"/>
/// 时释放，为渲染器实例级（非工厂共享 Device 单例）。桌面 GL 工厂额外持有独立的离屏上下文单例
/// （OpenGLOffscreenDeviceContext，共享组所有者），仅作 decode-init 阶段的中立设备句柄来源与
/// 零拷贝共享组根，不参与上屏绘制；Android GLES 当前无工厂级离屏设备上下文（零拷贝为未来增强）。</para>
/// <para>本接口为渲染器内部协作契约（internal，经 InternalsVisibleTo 供 OpenGL/OpenGLES 实现），
/// 不属对外公开 API；两渲染器共享同一声明，避免同签名接口双份漂移。</para>
/// </remarks>
internal interface IGlContext : IDisposable
{
    /// <summary>将上下文绑定到调用线程（成为当前 GL/GLES 上下文）。</summary>
    void MakeCurrent();

    /// <summary>解绑调用线程上的当前上下文（交还上下文，供其他线程经 <see cref="MakeCurrent"/> 重新绑定）。</summary>
    /// <remarks>GL/GLES 上下文具线程亲和性，同一时刻仅能在一个线程 current。渲染器每次 GL 操作完毕后调用本方法，
    /// 使 Present（管线线程）与 Detach / <see cref="IDisposable.Dispose"/>（主线程）可安全交替绑定。</remarks>
    void ReleaseCurrent();

    /// <summary>交换前后缓冲，呈现已渲染内容。</summary>
    void SwapBuffers();

    /// <summary>GL/GLES 主版本号（如 3）。</summary>
    int GlMajor { get; }

    /// <summary>GL/GLES 次版本号（桌面如 3，GLES 如 0）。</summary>
    int GlMinor { get; }

    /// <summary>上下文句柄（HGLRC / EGLContext）。桌面 GL 下兼作 <see cref="IGpuDeviceContext"/> 的 DeviceHandle / 共享组句柄。</summary>
    nint ContextHandle { get; }

    /// <summary>平台显示/设备上下文句柄（HDC / EGLDisplay）。桌面 GL 下兼作 <see cref="IGpuDeviceContext"/> 的 ContextHandle（解码侧 interop 用）。</summary>
    nint PlatformDisplay { get; }
}