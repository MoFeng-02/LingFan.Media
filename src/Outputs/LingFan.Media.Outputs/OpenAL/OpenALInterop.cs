using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.OpenAL;

/// <summary>
/// OpenAL 原生 API P/Invoke 绑定（跨平台：Windows / Linux / macOS / Android）。
/// </summary>
/// <remarks>
/// <para>覆盖推送式缓冲播放（alc* 设备/上下文 + al* 源/缓冲）。C 组 AUDIO-STUB 唯一遗留项的真实实现。</para>
/// <para><b>AOT 兼容</b>：纯 C API 直接 <see cref="LibraryImport"/>，零 COM、零反射、零动态代码。</para>
/// <para><b>原生库解析</b>：不同平台库名不同，统一用哨兵名 <c>"openal"</c>，
/// 由静态构造函数注册 <see cref="NativeLibrary.SetDllImportResolver"/> 按 OS 运行时映射到正确文件名。
/// 宿主须提供对应原生库（Windows=openal32.dll / Linux=libopenal.so.1 / macOS=libopenal.dylib / Android=libopenal.so）。</para>
/// </remarks>
internal static unsafe partial class OpenALInterop
{
    /// <summary>哨兵库名，由解析器映射到平台真实文件名。</summary>
    private const string LibraryName = "openal";

    static OpenALInterop()
    {
        // AOT 兼容：运行时解析，避免编译期写死平台库名（单一二进制跨平台）。
        NativeLibrary.SetDllImportResolver(typeof(OpenALInterop).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return IntPtr.Zero;

        // 按平台依次尝试候选文件名；全部失败返回 Zero（运行时抛 DllNotFoundException）。
        string[] candidates = OperatingSystem.IsWindows() ? new[] { "openal32.dll" }
            : OperatingSystem.IsLinux() ? new[] { "libopenal.so.1", "libopenal.so" }
            : OperatingSystem.IsMacOS() ? new[] { "libopenal.dylib", "OpenAL.framework/OpenAL" }
            : OperatingSystem.IsAndroid() ? new[] { "libopenal.so" }
            : new[] { "libopenal.so.1", "libopenal.so" };

        foreach (var cand in candidates)
        {
            try
            {
                return NativeLibrary.Load(cand);
            }
            catch (DllNotFoundException)
            {
                // 尝试下一个候选
            }
        }
        return IntPtr.Zero;
    }

    // ── ALC 常量 ──
    internal const int ALC_FALSE = 0;
    internal const int ALC_TRUE = 1;
    internal const int ALC_FREQUENCY = 0x1007;
    internal const int ALC_MONO_SOURCES = 0x1010;
    internal const int ALC_STEREO_SOURCES = 0x1011;

    // ── AL 常量 ──
    internal const int AL_INVALID = -1;
    internal const int AL_NONE = 0;
    internal const int AL_FALSE = 0;
    internal const int AL_TRUE = 1;
    internal const int AL_NO_ERROR = 0;
    internal const int AL_FORMAT_MONO8 = 0x1100;
    internal const int AL_FORMAT_STEREO8 = 0x1101;
    internal const int AL_FORMAT_MONO16 = 0x1102;
    internal const int AL_FORMAT_STEREO16 = 0x1103;
    internal const int AL_SOURCE_STATE = 0x1010;
    internal const int AL_INITIAL = 0x1011;
    internal const int AL_PLAYING = 0x1012;
    internal const int AL_PAUSED = 0x1013;
    internal const int AL_STOPPED = 0x1014;
    internal const int AL_BUFFERS_QUEUED = 0x1015;
    internal const int AL_BUFFERS_PROCESSED = 0x1016;
    internal const int AL_GAIN = 0x100A;

    // ── ALC 设备 / 上下文 ──

    [LibraryImport(LibraryName)]
    internal static partial IntPtr alcOpenDevice(byte* deviceName);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool alcCloseDevice(IntPtr device);

    [LibraryImport(LibraryName)]
    internal static partial IntPtr alcCreateContext(IntPtr device, int* attrList);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool alcMakeContextCurrent(IntPtr context);

    [LibraryImport(LibraryName)]
    internal static partial void alcDestroyContext(IntPtr context);

    [LibraryImport(LibraryName)]
    internal static partial int alcGetError(IntPtr device);

    // ── AL 源 / 缓冲 ──

    [LibraryImport(LibraryName)]
    internal static partial void alGenSources(int n, uint* sources);

    [LibraryImport(LibraryName)]
    internal static partial void alDeleteSources(int n, uint* sources);

    [LibraryImport(LibraryName)]
    internal static partial void alSourcef(uint source, int param, float value);

    [LibraryImport(LibraryName)]
    internal static partial void alSourcePlay(uint source);

    [LibraryImport(LibraryName)]
    internal static partial void alSourcePause(uint source);

    [LibraryImport(LibraryName)]
    internal static partial void alSourceStop(uint source);

    [LibraryImport(LibraryName)]
    internal static partial void alGetSourcei(uint source, int param, out int value);

    [LibraryImport(LibraryName)]
    internal static partial void alGenBuffers(int n, uint* buffers);

    [LibraryImport(LibraryName)]
    internal static partial void alDeleteBuffers(int n, uint* buffers);

    [LibraryImport(LibraryName)]
    internal static partial void alBufferData(uint buffer, int format, IntPtr data, int size, int freq);

    [LibraryImport(LibraryName)]
    internal static partial void alSourceQueueBuffers(uint source, int nb, uint* buffers);

    [LibraryImport(LibraryName)]
    internal static partial void alSourceUnqueueBuffers(uint source, int nb, uint* buffers);

    [LibraryImport(LibraryName)]
    internal static partial int alGetError();
}
