using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// FFmpeg MediaCodec 专用 C API 互操作。
/// </summary>
/// <remarks>
/// <para>FFmpeg.AutoGen 8.1.0 未生成 <c>libavcodec/jni.h</c> / <c>mediacodec.h</c> 的包装
/// （Android 专属头文件，经反射探针核验缺失）→ 此处直接 <c>LibraryImport("libavcodec.so")</c>。</para>
/// <para><b>平台边界</b>：仅 Android 运行时调用（调用点均有 <c>OperatingSystem.IsAndroid()</c> 守卫）；
/// 非 Android 平台仅编译不执行，不触发原生库加载。</para>
/// <para><b>AOT 兼容</b>：纯 P/Invoke，零反射、零动态代码。</para>
/// </remarks>
internal static partial class MediaCodecInterop
{
    /// <summary>
    /// 设置 JavaVM 指针（<c>av_jni_set_java_vm</c>）。MediaCodec 解码器内部经 JNI 调用 Java API，
    /// 宿主（net10.0-android）必须在首次打开 MediaCodec 解码器前调用一次
    /// （传 <c>JNIEnv.GetJavaVM()</c> 所得指针）。
    /// </summary>
    /// <param name="vm">JavaVM 指针。</param>
    /// <param name="logCtx">日志上下文（可为 <see cref="IntPtr.Zero"/>）。</param>
    /// <returns>0 成功；负值为 AVERROR。</returns>
    [LibraryImport("libavcodec.so", EntryPoint = "av_jni_set_java_vm")]
    internal static partial int SetJavaVM(IntPtr vm, IntPtr logCtx);

    /// <summary>
    /// 释放 MediaCodec 输出缓冲（<c>av_mediacodec_release_buffer</c>）。
    /// 表面直渲染模式下 <c>render=1</c> 将帧送显到绑定的 Surface；<c>render=0</c> 仅归还缓冲不渲染。
    /// </summary>
    /// <param name="buffer">AVMediaCodecBuffer 指针（表面模式 AVFrame 的 <c>data[3]</c>）。</param>
    /// <param name="render">1 = 渲染到 Surface；0 = 丢弃。</param>
    /// <returns>0 成功；负值为 AVERROR。</returns>
    [LibraryImport("libavcodec.so", EntryPoint = "av_mediacodec_release_buffer")]
    internal static partial int ReleaseBuffer(IntPtr buffer, int render);
}
