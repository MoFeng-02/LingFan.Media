namespace LingFan.Media.Renderers.Shared;

/// <summary>
/// Linux（纯 Wayland）渲染目标所需的原生窗口句柄封装——中性传输类型。
/// </summary>
/// <remarks>
/// <para>Vulkan Wayland Surface 需要同时持有 <c>wl_display*</c> 与 <c>wl_surface*</c> 两个指针，
/// 而 <see cref="LingFan.Media.Abstractions.IRenderTarget.NativeHandle"/> 为单一 <see cref="object"/>。
/// 故定义此中性传输类型，由上层（Avalonia / Wayland 集成层）在 Linux 下构造并传入；Windows 不使用。</para>
/// <para>本类型不引入 Wayland 绑定，仅消费已由 Wayland 客户端打开的 <c>wl_display*</c> / <c>wl_surface*</c> 指针，严守依赖倒置。
/// 与 <see cref="X11WindowHandle"/> 并列，供 Vulkan / OpenGL 渲染器在 Linux 上经原生 Surface 上屏。</para>
/// <para><b>分层归属</b>：与 <see cref="X11WindowHandle"/> 同源——纯 <see cref="IntPtr"/> 无绑定传输 DTO，<b>非契约类型</b>，
/// 归中性共享渲染层 <c>LingFan.Media.Renderers.Shared</c>（不放 Abstractions / Platforms）。</para>
/// <para><b>AOT 兼容</b>：record 为值语义传输类型，无反射。</para>
/// </remarks>
public sealed record WaylandWindowHandle(nint Display, nint Surface);
