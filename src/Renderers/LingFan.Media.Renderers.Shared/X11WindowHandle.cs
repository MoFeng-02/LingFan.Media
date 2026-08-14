namespace LingFan.Media.Renderers.Shared;

/// <summary>
/// Linux（X11 / EGL）渲染目标所需的原生窗口句柄封装——中性传输类型。
/// </summary>
/// <remarks>
/// <para>EGL 桌面 GL 上下文与 Vulkan Xlib Surface 均需要同时持有 X11 <c>Display*</c> 与原生 <c>Window</c> 两个指针，
/// 而 <see cref="LingFan.Media.Abstractions.IRenderTarget.NativeHandle"/> 为单一 <see cref="object"/>。
/// 故定义此中性传输类型，由上层（Avalonia / X11 集成层）在 Linux 下构造并传入；Windows 不使用。</para>
/// <para>Display 的获取（<c>XOpenDisplay</c>）由调用方负责，本类型不引入 X11 绑定，仅消费已打开的 Display 指针，
/// 严守依赖倒置与跨平台编译边界（Windows 上该类型存在但永不被构造）。</para>
/// <para><b>句柄语义</b>：本 record 仅用于 Linux X11 双指针（Display + Window）场景；Windows 不使用（其句柄由 WGL 上下文独立封装）。
/// Android 的 ANativeWindow 亦为单一 <see cref="IntPtr"/>（GLES 上下文路径），满足 <see cref="LingFan.Media.Abstractions.RenderHandleType"/>==<c>Pointer</c>，无需新増句柄类型；
/// Apple 平台不使用 OpenGL（由 Metal 后端覆盖）。</para>
/// <para><b>分层归属</b>：本类型是纯 <see cref="IntPtr"/> 无绑定传输 DTO，<b>非契约类型</b>——不放
/// <c>LingFan.Media.Abstractions</c>（契约层只放中立接口/语义枚举），亦不放 <c>LingFan.Media.Platforms</c>
/// （其承载 MF/VAAPI/VideoToolbox 等解码器平台服务，渲染器引用会撞 DIP 红线）；归中性共享渲染层
/// <c>LingFan.Media.Renderers.Shared</c>（两渲染器本就引用，零新增依赖、零 decoder 耦合）。</para>
/// <para><b>AOT 兼容</b>：record 为值语义传输类型，无反射。</para>
/// </remarks>
public sealed record X11WindowHandle(nint Display, nint Window);
