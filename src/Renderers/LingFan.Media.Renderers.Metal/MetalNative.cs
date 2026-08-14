using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// 零反射 Metal / Objective-C 运行时原生绑定层（替代 Metal.NET / SharpMetal 等第三方绑定库）。
/// </summary>
/// <remarks>
/// <para><b>设计目标</b>：彻底消除第三方绑定库的反射与运行期 marshaller——本层仅经
/// <see cref="LibraryImportAttribute">[LibraryImport]</see> 直接调用 Apple 原生符号，NativeAOT 下零 IL2xxx。</para>
/// <para><b>三层原生依赖</b>：</para>
/// <list type="bullet">
/// <item><c>libobjc</c>（/usr/lib/libobjc.dylib）：Objective-C 运行时——<c>objc_msgSend</c> / <c>objc_getClass</c> / <c>sel_registerName</c> / <c>objc_retain</c> / <c>objc_release</c> / 自动释放池。</item>
/// <item><c>Metal</c>（/System/Library/Frameworks/Metal.framework/Metal）：C 函数 <c>MTLCreateSystemDefaultDevice</c> 与 <c>MTLDevice</c> 等类（经 objc_msgSend 调用）。</item>
/// <item><c>QuartzCore</c>（CAMetalLayer）/ <c>Foundation</c>（NSString）：仅经 <c>objc_msgSend</c> 触达，不引入额外托管类型。</item>
/// </list>
/// <para><b>objc_msgSend 多固定签名重载</b>：AOT 源生成 marshaller 不支持变参 P/Invoke，故本层为 <c>objc_msgSend</c> 预声明
/// 多套固定签名（id 返回，按参数个数/类型区分）——这是 .NET NativeAOT 调 Objective-C 的权威做法。
/// 所有返回 id 的重载统一返回 <see cref="nint"/>，调用方按语义 cast。标量参数统一以 <c>nuint</c> 表示 NSUInteger、
/// <c>nint</c> 表示 id/指针，避免 <c>nint</c>/<c>nuint</c> 隐式转换导致的重载歧义。</para>
/// <para><b>跨平台守卫</b>：经 <see cref="NativeLibrary.SetDllImportResolver"/> 把中性库名重定向到 Apple framework 全路径；
/// 非 Apple 平台（Windows / Linux / Android）返回 <see cref="nint.Zero"/>（加载失败，fail-fast）。
/// 渲染器 <see cref="MetalRenderer.Attach"/> 以 <see cref="OperatingSystem.IsMacOS"/> / <see cref="OperatingSystem.IsIOS"/> 守卫，
/// 非 Apple 永不触发任何原生符号解析，故本机（Windows）可编译、可安全加载程序集，仅运行期在 Apple 平台真正调用。</para>
    /// <para><b>对象所有权（手动 retain/release，无 ARC）</b>：<c>alloc</c> / <c>new*</c> / <c>MTLCreateSystemDefaultDevice</c> 等方法按 Cocoa 规则返回
    /// <b>+1（已属调用方所有）</b>，调用方仅需在生命周期结束时调用<b>一次</b> <see cref="objc_release"/> 即可平衡——<b>切勿再额外 objc_retain</b>
    /// （否则 +1 永远无法释放、造成泄漏）。外部借入的对象（如宿主传入的 CAMetalLayer）才需 <see cref="objc_retain"/> 取得自己的 +1。
    /// 自动释放对象（<c>nextDrawable</c> / <c>renderPassDescriptor</c> / 各 property getter / 工厂方法）返回 autoreleased，
    /// 由每帧 <see cref="objc_autoreleasePoolPush"/> / <see cref="objc_autoreleasePoolPop"/> 回收，避免无 autorelease 池环境（NativeAOT）下的逐帧泄漏。</para>
/// <para><b>结构传参 ABI 约定</b>：AAPCS64（Apple Silicon）与 System V AMD64（Intel Mac）均规定
/// 聚合 &gt;16 字节时按指针传递。故 <see cref="MTLRegion"/>（48 字节）、<see cref="MTLClearColor"/>（32 字节）以 <c>ref</c> 传递；
/// <c>CGSize</c>（16 字节，setDrawableSize:）以两个 <c>double</c> 按值传递（ARM64 HFA / x86_64 SSE 寄存器一致）。
/// 本层刻意避开一切 ≤16 字节但非 HFA 的结构按值传参（如 CGRect），以降低架构相关风险。</para>
/// <para><b>AOT 兼容</b>：static partial 类，无反射、无 <c>[ComImport]</c>；全部 <c>[LibraryImport]</c> 静态解析。</para>
/// </remarks>
internal static unsafe partial class MetalNative
{
    // ── 中性库名 → Apple framework 全路径（非 Apple 返回 Zero，fail-fast）──
    static MetalNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MetalNative).Assembly, ResolveLoader);

        // 确保 Foundation 已加载——NSString 等类由 Foundation 定义，纯 .NET 进程可能尚未加载。
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            NativeLibrary.TryLoad(
                "/System/Library/Frameworks/Foundation.framework/Foundation",
                typeof(MetalNative).Assembly, null, out _);
        }
    }

    private static nint ResolveLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, "libobjc", StringComparison.Ordinal))
        {
            nint h;
            return NativeLibrary.TryLoad("/usr/lib/libobjc.dylib", assembly, searchPath, out h) ? h : nint.Zero;
        }
        if (string.Equals(libraryName, "Metal", StringComparison.Ordinal))
        {
            nint h;
            return NativeLibrary.TryLoad("/System/Library/Frameworks/Metal.framework/Metal", assembly, searchPath, out h) ? h : nint.Zero;
        }
        if (string.Equals(libraryName, "QuartzCore", StringComparison.Ordinal))
        {
            nint h;
            return NativeLibrary.TryLoad("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", assembly, searchPath, out h) ? h : nint.Zero;
        }
        if (string.Equals(libraryName, "Foundation", StringComparison.Ordinal))
        {
            nint h;
            return NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", assembly, searchPath, out h) ? h : nint.Zero;
        }
        return nint.Zero;
    }

    // ── Objective-C 运行时 C 函数（libobjc）──

    [LibraryImport("libobjc", EntryPoint = "objc_getClass")]
    public static partial nint objc_getClass(byte* name);

    [LibraryImport("libobjc", EntryPoint = "sel_registerName")]
    public static partial nint sel_registerName(byte* name);

    [LibraryImport("libobjc", EntryPoint = "objc_retain")]
    public static partial nint objc_retain(nint obj);

    [LibraryImport("libobjc", EntryPoint = "objc_release")]
    public static partial void objc_release(nint obj);

    [LibraryImport("libobjc", EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport("libobjc", EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void objc_autoreleasePoolPop(nint pool);

    // ── Metal C 函数（Metal.framework）──

    [LibraryImport("Metal", EntryPoint = "MTLCreateSystemDefaultDevice")]
    public static partial nint MTLCreateSystemDefaultDevice();

    // ── objc_msgSend 多固定签名重载（id 返回 = nint；标量 NSUInteger 用 nuint，id/指针用 nint）──

    // 无参（getter / 工厂 / alloc / init / 命令缓冲 / drawable / commit 等）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector);

    // 1 个 id 参数（setDevice: / setLayer: / setVertexFunction: / setFragmentFunction: / newFunctionWithName: / newTextureWithDescriptor: / presentDrawable: / renderCommandEncoderWithDescriptor: / setRenderPipelineState: / setTexture: 等）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0);

    // 1 个 NSUInteger 参数（setPixelFormat: / setLoadAction: / setStoreAction: / objectAtIndexedSubscript: 等）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg0);

    // 1 个 BOOL/char 参数（setOpaque: / setWantsLayer: / mipmapped: 等）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, byte arg0);

    // CGSize 按值（setDrawableSize:，16 字节 HFA，两个 double）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, double a, double b);

    // C 字符串参数（stringWithUTF8String:）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, byte* arg0);

    // id + NSUInteger（setFragmentTexture:atIndex: 等）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nuint arg1);

    // id + 2×NSUInteger（setVertexBuffer:offset:atIndex: / setFragmentBytes:length:atIndex: / newBufferWithBytes:length:options: 等）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nuint arg1, nuint arg2);

    // 3×NSUInteger（texture2DDescriptorWithPixelFormat:width:height:mipmapped:，末参 byte）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg0, nuint arg1, nuint arg2, byte arg3);

    // id + 错误指针（newRenderPipelineStateWithDescriptor:error:）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nint* arg1);

    // id + id + 错误指针（newLibraryWithSource:options:error:）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg0, nint arg1, nint* arg2);

    // 3×NSUInteger（drawPrimitives:vertexStart:vertexCount:）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg0, nuint arg1, nuint arg2);

    // MTLRegion（>16 字节，按 ref/指针传递）+ 3×NSUInteger（replaceRegion:mipmapLevel:withBytes:bytesPerRow:）。
    // 独立方法名（非 objc_msgSend 重载）：避免与 texture2DDescriptor 的 6 参数值重载（nuint,nuint,nuint,byte）在
    // 重载解析时因 nint/nuint/byte 互隐式转换而产生歧义（CS1615/CS1503）。EntryPoint 仍为原生 objc_msgSend。
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSendReplaceRegion(nint receiver, nint selector, ref MTLRegion region, nuint level, nint bytes, nuint bytesPerRow);

    // MTLClearColor（>16 字节，按 ref/指针传递）（setClearColor:）
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, ref MTLClearColor clearColor);

    // ── 结构类型（AOT 兼容：LayoutKind.Sequential，按 ABI 逐字段映射）──

    /// <summary>MTLRegion——纹理替换区域（origin + size，6 × NSUInteger = 48 字节，&gt;16 字节按指针传递）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MTLRegion
    {
        public nuint X, Y, Z;
        public nuint Width, Height, Depth;
    }

    /// <summary>MTLClearColor——渲染Pass清屏色（4 × double = 32 字节，&gt;16 字节按指针传递）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MTLClearColor
    {
        public double Red, Green, Blue, Alpha;
    }

    // ── 安全包装（托管侧便捷方法，非 P/Invoke）──

    /// <summary>按名称取得 Objective-C 类对象（<c>objc_getClass</c>）。</summary>
    internal static nint Class(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name))
            return objc_getClass(p);
    }

    /// <summary>按名称注册 selector（<c>sel_registerName</c>）。</summary>
    internal static nint Sel(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name))
            return sel_registerName(p);
    }

    /// <summary>由 C# 字符串创建 NSString（<c>[NSString stringWithUTF8String:]</c>，自动释放，须处于 autorelease 池内）。</summary>
    internal static nint MakeNSString(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        byte[] withNull = new byte[utf8.Length + 1];
        Buffer.BlockCopy(utf8, 0, withNull, 0, utf8.Length);
        fixed (byte* p = withNull)
            return objc_msgSend(Class("NSString"), Sel("stringWithUTF8String:"), p);
    }

    /// <summary>[[Cls alloc] init]——创建并持有（+1）一个 Objective-C 对象。</summary>
    internal static nint AllocInit(nint cls)
    {
        nint obj = objc_msgSend(cls, Sel("alloc"));
        return objc_msgSend(obj, Sel("init"));
    }
}
