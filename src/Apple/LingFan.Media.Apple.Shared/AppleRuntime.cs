using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.Apple.Shared;

/// <summary>
/// 零反射 Apple / Objective-C 运行时原生绑定层（替代 Metal.NET / SharpMetal 等第三方绑定库）。
/// </summary>
/// <remarks>
/// <para><b>设计目标</b>：彻底消除第三方绑定库的反射与运行期 marshaller——本层仅经
/// <see cref="LibraryImportAttribute">[LibraryImport]</see> 直接调用 Apple 原生符号，NativeAOT 下零 IL2xxx。</para>
/// <para><b>全仓唯一 Apple 绑定源</b>：Metal 渲染器、Backends.Apple、Platforms 的 VideoToolbox / Metal interop
/// 全部消费本层，避免多处 Apple 绑定分叉。</para>
/// <para><b>三层原生依赖</b>：</para>
/// <list type="bullet">
/// <item><c>libobjc</c>（/usr/lib/libobjc.dylib）：Objective-C 运行时——<c>objc_msgSend</c> / <c>objc_getClass</c> / <c>sel_registerName</c> / <c>objc_retain</c> / <c>objc_release</c> / 自动释放池。</item>
/// <item><c>Metal</c>（/System/Library/Frameworks/Metal.framework/Metal）：C 函数 <c>MTLCreateSystemDefaultDevice</c> 与 <c>MTLDevice</c> 等类（经 objc_msgSend 调用）。</item>
/// <item><c>Foundation</c> / <c>QuartzCore</c> / <c>CoreFoundation</c> / <c>CoreVideo</c> / <c>CoreMedia</c> / <c>VideoToolbox</c> / <c>AVFoundation</c> / <c>IOSurface</c> 等框架：仅经 <c>objc_msgSend</c> 或对应 C API 触达。</item>
/// </list>
/// <para><b>objc_msgSend 多固定签名重载</b>：AOT 源生成 marshaller 不支持变参 P/Invoke，故本层为 <c>objc_msgSend</c> 预声明
/// 多套固定签名（id 返回，按参数个数/类型区分）——这是 .NET NativeAOT 调 Objective-C 的权威做法。
/// 所有返回 id 的重载统一返回 <see cref="nint"/>，调用方按语义 cast。标量参数统一以 <c>nuint</c> 表示 NSUInteger、
/// <c>nint</c> 表示 id/指针，避免 <c>nint</c>/<c>nuint</c> 隐式转换导致的重载歧义。</para>
/// <para><b>跨平台守卫</b>：经 <see cref="NativeLibrary.SetDllImportResolver"/> 把中性库名重定向到 Apple framework 全路径；
/// 非 Apple 平台（Windows / Linux / Android）返回 <see cref="nint.Zero"/>（加载失败，fail-fast）。
/// 渲染器 <see cref="OperatingSystem.IsMacOS"/> / <see cref="OperatingSystem.IsIOS"/> 守卫，
/// 非 Apple 永不触发任何原生符号解析，故非 Apple 平台可编译、可安全加载程序集，仅运行期在 Apple 平台真正调用。</para>
/// <para><b>对象所有权（手动 retain/release，无 ARC）</b>：<c>alloc</c> / <c>new*</c> / <c>MTLCreateSystemDefaultDevice</c> 等方法按 Cocoa 规则返回
/// <b>+1（已属调用方所有）</b>，调用方仅需在生命周期结束时调用<b>一次</b> <see cref="objc_release"/> 即可平衡——<b>切勿再额外 objc_retain</b>
/// （否则 +1 永远无法释放、造成泄漏）。外部借入的对象（如宿主传入的 CAMetalLayer）才需 <see cref="objc_retain"/> 取得自己的 +1。
/// 自动释放对象（<c>nextDrawable</c> / <c>renderPassDescriptor</c> / 各 property getter / 工厂方法）返回 autoreleased，
/// 由每帧 <see cref="objc_autoreleasePoolPush"/> / <see cref="objc_autoreleasePoolPop"/> 回收，避免无 autorelease 池环境（NativeAOT）下的逐帧泄漏。
/// 可用 <see cref="SafeAppleObject"/> / <see cref="SafeCFTypeRef"/> 把所有权交给 SafeHandle，确定性释放（防泄漏）。</para>
/// <para><b>结构传参 ABI 约定</b>：AAPCS64（Apple Silicon）与 System V AMD64（Intel Mac）均规定
/// 聚合 &gt;16 字节时按指针传递。故 <see cref="MTLRegion"/>（48 字节）、<see cref="MTLClearColor"/>（32 字节）以 <c>ref</c> 传递；
/// <c>CGSize</c>（16 字节，setDrawableSize:）以两个 <c>double</c> 按值传递（ARM64 HFA / x86_64 SSE 寄存器一致）。
/// 本层刻意避开一切 ≤16 字节但非 HFA 的结构按值传参（如 CGRect），以降低架构相关风险。</para>
/// <para><b>AOT 兼容</b>：static partial 类，无反射、无 <c>[ComImport]</c>；全部 <c>[LibraryImport]</c> 静态解析。</para>
/// </remarks>
public static unsafe partial class AppleRuntime
{
    // 中性库名 → Apple framework 全路径（非 Apple 返回 Zero，fail-fast）
    static AppleRuntime()
    {
        NativeLibrary.SetDllImportResolver(typeof(AppleRuntime).Assembly, ResolveLoader);

        // 确保 Foundation 已加载——NSString 等类由 Foundation 定义，纯 .NET 进程可能尚未加载。
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            NativeLibrary.TryLoad(
                "/System/Library/Frameworks/Foundation.framework/Foundation",
                typeof(AppleRuntime).Assembly, null, out _);
        }
    }

    /// <summary>中性库名 → Apple framework 全路径（与 <see cref="ResolveAppleFramework"/> 共用同一张映射表）。</summary>
    private static string? _FrameworkPath(string libraryName) => libraryName switch
    {
        "libobjc" => "/usr/lib/libobjc.dylib",
        "Foundation" => "/System/Library/Frameworks/Foundation.framework/Foundation",
        "Metal" => "/System/Library/Frameworks/Metal.framework/Metal",
        "QuartzCore" => "/System/Library/Frameworks/QuartzCore.framework/QuartzCore",
        "CoreFoundation" => "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
        "CoreVideo" => "/System/Library/Frameworks/CoreVideo.framework/CoreVideo",
        "CoreMedia" => "/System/Library/Frameworks/CoreMedia.framework/CoreMedia",
        "VideoToolbox" => "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox",
        "AVFoundation" => "/System/Library/Frameworks/AVFoundation.framework/AVFoundation",
        "CoreGraphics" => "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
        "AudioToolbox" => "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox",
        "IOSurface" => "/System/Library/Frameworks/IOSurface.framework/IOSurface",
        _ => null,
    };

    /// <summary>
    /// 把中性库名解析为 Apple framework 全路径并加载（供任意消费程序集复用，消除 resolver 逻辑分叉）。
    /// </summary>
    /// <param name="libraryName">中性库名（libobjc / Metal / Foundation / VideoToolbox / AVFoundation …）。</param>
    /// <param name="assembly">调用方程序集（P/Invoke 解析按调用程序集进行，每个消费程序集须各自注册）。</param>
    /// <param name="searchPath">原生搜索路径（透传）。</param>
    /// <returns>已加载库句柄；非 Apple 平台或未知框架返回 <see cref="nint.Zero"/>。</returns>
    public static nint ResolveAppleFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? path = _FrameworkPath(libraryName);
        if (path is null)
            return nint.Zero;

        nint h;
        return NativeLibrary.TryLoad(path, assembly, searchPath, out h) ? h : nint.Zero;
    }

    /// <summary>
    /// 解析 Apple framework 中的全局 C 符号（如 <c>kCFAllocatorNull</c> / <c>kCVPixelBufferPixelFormatTypeKey</c> 等
    /// <c>extern</c> 常量或类对象）。返回符号地址（指针；对于 <c>NSString*</c> / <c>CFTypeRef</c> 常量，需再按语义解引用）。
    /// 非 Apple 平台或未知符号返回 <see cref="nint.Zero"/>。
    /// </summary>
    public static nint GetGlobalSymbol(string framework, string symbol)
    {
        string? path = _FrameworkPath(framework);
        if (path is null) return nint.Zero;
        if (!NativeLibrary.TryLoad(path, typeof(AppleRuntime).Assembly, null, out nint h)) return nint.Zero;
        return NativeLibrary.GetExport(h, symbol);
    }

    /// <summary>
    /// 为指定消费程序集注册 Apple framework 解析器（幂等）。
    /// </summary>
    /// <remarks>每个经 [LibraryImport] 调用 Apple 原生符号的程序集（Metal / Backends.Apple / Platforms）须各自注册，
    /// 因为 P/Invoke 解析按调用程序集进行。</remarks>
    /// <param name="assembly">消费程序集。</param>
    public static void EnsureResolverRegistered(Assembly assembly)
    {
        NativeLibrary.SetDllImportResolver(assembly, ResolveLoader);
    }

    private static nint ResolveLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        => ResolveAppleFramework(libraryName, assembly, searchPath);

    // Objective-C 运行时 C 函数（libobjc）

    /// <summary>按名称取得 Objective-C 类对象（类不存在返回 <see cref="nint.Zero"/>）。</summary>
    /// <param name="name">UTF-8 NUL 结尾的类名。</param>
    [LibraryImport("libobjc", EntryPoint = "objc_getClass")]
    public static partial nint objc_getClass(byte* name);

    /// <summary>按名称注册（或取得已注册的）selector。</summary>
    /// <param name="name">UTF-8 NUL 结尾的 selector 名（如 <c>alloc</c> / <c>nextDrawable</c>）。</param>
    [LibraryImport("libobjc", EntryPoint = "sel_registerName")]
    public static partial nint sel_registerName(byte* name);

    /// <summary>引用计数 +1（仅用于借入对象取得自己的所有权；+1 返回的对象切勿再 retain）。</summary>
    /// <param name="obj">Objective-C 对象指针。</param>
    [LibraryImport("libobjc", EntryPoint = "objc_retain")]
    public static partial nint objc_retain(nint obj);

    /// <summary>引用计数 -1（与 retain / +1 所有权一一配对，归零即销毁）。</summary>
    /// <param name="obj">Objective-C 对象指针。</param>
    [LibraryImport("libobjc", EntryPoint = "objc_release")]
    public static partial void objc_release(nint obj);

    /// <summary>压入一层 autorelease 池，返回池标记（供 <see cref="objc_autoreleasePoolPop"/> 配对弹出）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint objc_autoreleasePoolPush();

    /// <summary>弹出 autorelease 池并释放池内全部 autoreleased 对象（须与 Push 严格配对）。</summary>
    /// <param name="pool"><see cref="objc_autoreleasePoolPush"/> 返回的池标记。</param>
    [LibraryImport("libobjc", EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void objc_autoreleasePoolPop(nint pool);

    // CoreFoundation 引用计数（IOSurface / CVPixelBuffer 等 CF 类型）

    /// <summary>CF 引用计数 +1（IOSurface / CVPixelBuffer 等 CF 类型；+1 返回的对象切勿再 retain）。</summary>
    /// <param name="cf">CF 对象指针。</param>
    [LibraryImport("CoreFoundation", EntryPoint = "CFRetain")]
    public static partial nint CFRetain(nint cf);

    /// <summary>CF 引用计数 -1（与 CFRetain / +1 所有权一一配对；传入 Zero 会崩溃，调用方须先判空）。</summary>
    /// <param name="cf">CF 对象指针。</param>
    [LibraryImport("CoreFoundation", EntryPoint = "CFRelease")]
    public static partial void CFRelease(nint cf);

    // Metal C 函数（Metal.framework）

    /// <summary>取得系统默认 Metal 设备（按 Cocoa 规则返回 +1，调用方负责一次 release；无 Metal 设备返回 Zero）。</summary>
    [LibraryImport("Metal", EntryPoint = "MTLCreateSystemDefaultDevice")]
    public static partial nint MTLCreateSystemDefaultDevice();

    // objc_msgSend 多固定签名重载（id 返回 = nint；标量 NSUInteger 用 nuint，id/指针用 nint）

    /// <summary>无参消息发送（getter / 工厂 / alloc / init / 命令缓冲 / drawable / commit 等）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector);

    /// <summary>1 个 id 参数（setDevice: / setLayer: / setVertexFunction: / setFragmentFunction: / newFunctionWithName: / newTextureWithDescriptor: / presentDrawable: / renderCommandEncoderWithDescriptor: / setRenderPipelineState: / setTexture: 等）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0);

    /// <summary>1 个 NSUInteger 参数（setPixelFormat: / setLoadAction: / setStoreAction: / objectAtIndexedSubscript: 等）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg0);

    /// <summary>1 个 BOOL/char 参数（setOpaque: / setWantsLayer: / mipmapped: 等）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, byte arg0);

    /// <summary>CGSize 按值（setDrawableSize:，16 字节 HFA，两个 double）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, double a, double b);

    /// <summary>C 字符串参数（stringWithUTF8String:）。</summary>
    /// <param name="receiver">objc 对象（id）。</param>
    /// <param name="selector">objc 选择器（SEL）。</param>
    /// <param name="arg0">UTF-8 NUL 结尾字符串指针。</param>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, byte* arg0);

    /// <summary>id + NSUInteger（setFragmentTexture:atIndex: 等）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nuint arg1);

    /// <summary>id + 2×NSUInteger（setVertexBuffer:offset:atIndex: / setFragmentBytes:length:atIndex: / newBufferWithBytes:length:options: 等）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nuint arg1, nuint arg2);

    /// <summary>3×NSUInteger + byte（texture2DDescriptorWithPixelFormat:width:height:mipmapped:，末参 BOOL）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg0, nuint arg1, nuint arg2, byte arg3);

    /// <summary>id + 错误指针（newRenderPipelineStateWithDescriptor:error:，失败时经 arg1 返回 NSError）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nint* arg1);

    /// <summary>id + id + 错误指针（newLibraryWithSource:options:error:，失败时经 arg2 返回 NSError）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nint arg1, nint* arg2);

    /// <summary>3×NSUInteger（drawPrimitives:vertexStart:vertexCount:）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg0, nuint arg1, nuint arg2);

    /// <summary>
    /// replaceRegion:mipmapLevel:withBytes:bytesPerRow:——MTLRegion（&gt;16 字节）按 ref/指针传递 + 3×NSUInteger。
    /// </summary>
    /// <remarks>独立方法名（非 objc_msgSend 重载）：避免与 texture2DDescriptor 的 6 参数值重载（nuint,nuint,nuint,byte）在
    /// 重载解析时因 nint/nuint/byte 互隐式转换而产生歧义（CS1615/CS1503）。EntryPoint 仍为原生 objc_msgSend。</remarks>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSendReplaceRegion(nint receiver, nint selector, ref MTLRegion region, nuint level, nint bytes, nuint bytesPerRow);

    /// <summary>MTLClearColor（&gt;16 字节，按 ref/指针传递）（setClearColor:）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, ref MTLClearColor clearColor);

    // 结构类型（AOT 兼容：LayoutKind.Sequential，按 ABI 逐字段映射）

    /// <summary>MTLRegion——纹理替换区域（origin + size，6 × NSUInteger = 48 字节，&gt;16 字节按指针传递）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLRegion
    {
        /// <summary>区域原点坐标（X / Y / Z，像素）。</summary>
        public nuint X, Y, Z;
        /// <summary>区域尺寸（Width / Height / Depth，像素）。</summary>
        public nuint Width, Height, Depth;
    }

    /// <summary>MTLClearColor——渲染Pass清屏色（4 × double = 32 字节，&gt;16 字节按指针传递）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLClearColor
    {
        /// <summary>清屏色分量（Red / Green / Blue / Alpha，通常 0..1，按附件像素格式解释）。</summary>
        public double Red, Green, Blue, Alpha;
    }

    // 安全包装（托管侧便捷方法，非 P/Invoke）

    /// <summary>按名称取得 Objective-C 类对象（<c>objc_getClass</c>）。</summary>
    public static nint Class(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name))
            return objc_getClass(p);
    }

    /// <summary>按名称注册 selector（<c>sel_registerName</c>）。</summary>
    public static nint Sel(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name))
            return sel_registerName(p);
    }

    /// <summary>由 C# 字符串创建 NSString（<c>[NSString stringWithUTF8String:]</c>，自动释放，须处于 autorelease 池内）。</summary>
    public static nint MakeNSString(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        byte[] withNull = new byte[utf8.Length + 1];
        Buffer.BlockCopy(utf8, 0, withNull, 0, utf8.Length);
        fixed (byte* p = withNull)
            return objc_msgSend(Class("NSString"), Sel("stringWithUTF8String:"), p);
    }

    /// <summary>[[Cls alloc] init]——创建并持有（+1）一个 Objective-C 对象。</summary>
    public static nint AllocInit(nint cls)
    {
        nint obj = objc_msgSend(cls, Sel("alloc"));
        return objc_msgSend(obj, Sel("init"));
    }
}

/// <summary>
/// Objective-C 对象的所有权 SafeHandle：构造时持有既有的 +1 对象（不额外 retain），
/// <see cref="ReleaseHandle"/> 调用一次 <see cref="AppleRuntime.objc_release"/> 平衡引用计数。
/// </summary>
/// <remarks>
/// <para>用于把 Cocoa 所有权模型接入确定性释放，防止引用计数泄漏。
/// 仅适用于按 Cocoa 规则返回 +1 的对象（alloc / new* / MTLCreateSystemDefaultDevice 等）；
/// autoreleased 对象（getter / 工厂方法）不应包入本句柄，应由 autorelease 池回收。</para>
/// </remarks>
public sealed class SafeAppleObject : SafeHandle
{
    /// <summary>以既有的 +1 原生对象构造（不再额外 retain）。</summary>
    /// <param name="ptr">已 +1 的原生对象指针。</param>
    /// <param name="ownsHandle">是否拥有并负责释放（默认 true）。</param>
    public SafeAppleObject(nint ptr, bool ownsHandle = true) : base(nint.Zero, ownsHandle)
    {
        SetHandle(ptr);
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == nint.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (handle != nint.Zero)
            AppleRuntime.objc_release(handle);
        return true;
    }
}

/// <summary>
/// CoreFoundation 类型（IOSurfaceRef / CVPixelBufferRef 等）的所有权 SafeHandle：
/// 构造时持有既有的 +1 引用（不额外 retain），<see cref="ReleaseHandle"/> 调用一次
/// <see cref="AppleRuntime.CFRelease"/> 平衡引用计数。
/// </summary>
public sealed class SafeCFTypeRef : SafeHandle
{
    /// <summary>以既有的 +1 CF 对象构造（不再额外 retain）。</summary>
    /// <param name="ptr">已 +1 的 CF 对象指针。</param>
    /// <param name="ownsHandle">是否拥有并负责释放（默认 true）。</param>
    public SafeCFTypeRef(nint ptr, bool ownsHandle = true) : base(nint.Zero, ownsHandle)
    {
        SetHandle(ptr);
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == nint.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (handle != nint.Zero)
            AppleRuntime.CFRelease(handle);
        return true;
    }
}
