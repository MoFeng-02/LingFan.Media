using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// Linux 导入分部：EGL_EXT_image_dma_buf_import（VAAPI dma_buf → GL 纹理），含平面属性表与纹理参数。
/// </summary>
public sealed partial class OpenGLGpuFrameProducer
{
    private unsafe bool TryImportLinuxVaApi(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;

        // 须先建立并绑定 EGL/GL 上下文：EGL 扩展函数经 eglGetProcAddress 解析，仅当 EGL 上下文 current 时返回有效指针。
        // 可用性探测必须发生在 MakeCurrent 之后，否则误判"不可用"并经静态缓存永久禁用零拷贝路径。
        // 整个 EGL 段须在同一锁内串行（同 WGL 路径：共享组所有者上下文不能并发 current 于两线程）。
        _glContext.EnsureCreated();
        lock (_glContext.GlAccessLock)
        {
            _glContext.MakeCurrent();
            nint eglImageY = nint.Zero, eglImageUV = nint.Zero;
            uint texY = 0, texUV = 0;
            int fd = source.Handle == IntPtr.Zero ? -1 : (int)source.Handle;
            try
            {
                if (!EglDmaBufImport.IsEglDmaBufImportAvailable())
                {
                    _logger?.LogWarning("[OPENGL-ZEROCOPY] EGL_EXT_image_dma_buf_import 不可用，回落软件解码。");
                    CloseFd(fd); // 失败出口：fd 尚未被消费，须关闭防泄漏（单一责任人）
                    return false;
                }

                nint display = _glContext.OffscreenDisplay; // EGLDisplay（离屏共享组所有者）

                // 显示级能力自报（S_OK≠被接受）：函数指针可解析 ≠ 本显示支持 dma_buf 导入；
                // Mesa 对不支持导入的显示直接以 EGL_BAD_PARAMETER 拒绝 eglCreateImageKHR。
                nint dispExtsPtr = EglNative.eglQueryString(display, EglNative.EglDeviceExtensions);
                string dispExts = dispExtsPtr != nint.Zero
                    ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(dispExtsPtr) ?? string.Empty
                    : string.Empty;
                if (!dispExts.Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal))
                {
                    _logger?.LogWarning(
                        "[OPENGL-ZEROCOPY] 离屏显示缺 EGL_EXT_image_dma_buf_import —— 本显示不支持 dma_buf 导入，回落软件解码。");
                    CloseFd(fd); // 失败出口：fd 尚未被消费，须关闭防泄漏（单一责任人）
                    return false;
                }

                // DRM_FORMAT_MOD_INVALID（0x00FFFFFFFFFFFFFF）不可进导入属性表（驱动必拒）：按无 modifier（线性）处理。
                const ulong DrmFormatModInvalid = 0x00FFFFFFFFFFFFFFUL;
                bool hasModifier = source.DrmModifier != 0 && source.DrmModifier != DrmFormatModInvalid;

                uint yOffset = source.PlaneOffsets?.Length > 0 ? source.PlaneOffsets[0] : 0u;
                uint yPitch = source.PlanePitches?.Length > 0 ? source.PlanePitches[0] : (uint)source.Width;

                // Y 平面：单平面 R8（DRM_FORMAT_R8 = 0x20203852）。composed NV12 双平面共享同一 fd。
                eglImageY = CreateDmaBufPlaneImage(display, fd, source.Width, source.Height, yOffset, yPitch, 0x20203852, hasModifier, source.DrmModifier);
                if (eglImageY == nint.Zero && hasModifier)
                {
                    // tiling 源（Y_TILED 等）按 linear 导入是定义性错误（数据布局不匹配 → 马赛克）——不降级。
                    // 显示侧 modifiers 列表不支持该组合时，正确出路是 CPU 传输（画面正确，无零拷贝）。
                    _logger?.LogWarning(
                        "[OPENGL-ZEROCOPY] Y 平面带 modifier 导入被拒（eglErr=0x{Err:X8}，modifier=0x{Mod:X16}）——本显示不支持该 tiling 组合，回落 CPU 传输（画面正确，无零拷贝）。",
                        EglNative.eglGetError(), source.DrmModifier);
                    LogDisplayDmaBufModifiers(display, 0x20203852);
                    CloseFd(fd); // 失败出口：fd 尚未被消费，须关闭防泄漏（单一责任人）
                    return false;
                }
                if (eglImageY == nint.Zero)
                {
                    _logger?.LogWarning(
                        "[OPENGL-ZEROCOPY] eglCreateImageKHR(Y 平面) 失败 eglErr=0x{Err:X8} —— fourcc=0x{Fourcc:X8} modifier=0x{Mod:X16} pitch={Pitch} offset={Offset} 尺寸={W}x{H} display=0x{Dsp:X8}，回落软件解码。",
                        EglNative.eglGetError(), (uint)source.DrmFourcc, source.DrmModifier, yPitch, yOffset,
                        source.Width, source.Height, display);
                    CloseFd(fd); // 失败出口：fd 尚未被消费，须关闭防泄漏（单一责任人）
                    return false;
                }

                // UV 平面：单平面 GR88（DRM_FORMAT_GR88 = 0x38385247），与 Y 同 fd、独立 offset/pitch、同 modifier。
                // 注：EGL 导入为 RG8 纹理后 .rg = (U, V)，与 NV12(UV 交错) 含义一致（uSwap=0）。
                uint uvOffset = source.PlaneOffsets?.Length > 1 ? source.PlaneOffsets[1]
                    : (uint)(source.Height * (source.PlanePitches?.Length > 0 ? source.PlanePitches[0] : (uint)source.Width));
                uint uvPitch = source.PlanePitches?.Length > 1 ? source.PlanePitches[1] : (uint)source.Width;
                eglImageUV = CreateDmaBufPlaneImage(display, fd, (int)(source.Width / 2), (int)(source.Height / 2), uvOffset, uvPitch, 0x38385247, hasModifier, source.DrmModifier);
                if (eglImageUV == nint.Zero)
                {
                    _logger?.LogWarning(
                        "[OPENGL-ZEROCOPY] eglCreateImageKHR(UV 平面) 失败 eglErr=0x{Err:X8} useModifier={UseModifier}，回落软件解码。",
                        EglNative.eglGetError(), hasModifier);
                    // 失败出口：Y 平面 EGLImage 已建须销毁；fd 尚未被消费，须关闭防泄漏（单一责任人）
                    EglDmaBufImport.EglDestroyImageKHR(display, eglImageY);
                    CloseFd(fd);
                    return false;
                }

                GLNative.glGenTextures(1, &texY);
                GLNative.glBindTexture(GLNative.GlTexture2DConst, texY);
                EglDmaBufImport.GlEGLImageTargetTexture2DOES((uint)GLNative.GlTexture2DConst, eglImageY);
                SetDmaBufTexParams();

                GLNative.glGenTextures(1, &texUV);
                GLNative.glBindTexture(GLNative.GlTexture2DConst, texUV);
                EglDmaBufImport.GlEGLImageTargetTexture2DOES((uint)GLNative.GlTexture2DConst, eglImageUV);
                SetDmaBufTexParams();

                texture = new GLDmaBufNv12Texture(
                    width: source.Width, height: source.Height,
                    yTexture: texY, uvTexture: texUV,
                    eglDisplay: display, eglImageY: eglImageY, eglImageUV: eglImageUV,
                    glContext: _glContext);

                // fd 已被两个 EGLImage 导入（dma_buf 由 EGLImage 持有独立引用），关闭 fd 防泄漏（单一责任人）
                CloseFd(fd);
                return true;
            }
            catch
            {
                if (texY != 0) GLNative.glDeleteTextures(1, &texY);
                if (texUV != 0) GLNative.glDeleteTextures(1, &texUV);
                if (eglImageY != nint.Zero) EglDmaBufImport.EglDestroyImageKHR(_glContext.OffscreenDisplay, eglImageY);
                if (eglImageUV != nint.Zero) EglDmaBufImport.EglDestroyImageKHR(_glContext.OffscreenDisplay, eglImageUV);
                if (fd >= 0) CloseFd(fd); // 失败出口：fd 尚未被消费，须关闭防泄漏
                throw;
            }
            finally
            {
                _glContext.ReleaseCurrent();
            }
        }
    }

    /// <summary>诊断：查询并打印显示侧指定 fourcc 支持的 dma_buf modifier 列表与 external_only 标志（EGL_EXT_image_dma_buf_import_modifiers）。</summary>
    private unsafe void LogDisplayDmaBufModifiers(nint display, int drmFourcc)
    {
        int num = 0;
        if (!EglDmaBufImport.TryQueryDmaBufModifiers(display, drmFourcc, null, null, 0, &num) || num <= 0)
        {
            _logger?.LogInformation(
                "[OPENGL-ZEROCOPY] 显示对 fourcc=0x{Fourcc:X8} 的 modifier 查询为空/失败——导入支持面可能以 linear 为限。",
                drmFourcc);
            return;
        }
        var modifiers = new ulong[num];
        var externalOnly = new int[num];
        fixed (ulong* m = modifiers)
        fixed (int* e = externalOnly)
        {
            int count = num;
            _ = EglDmaBufImport.TryQueryDmaBufModifiers(display, drmFourcc, m, e, num, &count);
        }
        var list = string.Join(
            ", ",
            modifiers.Take(num).Select((v, i) => $"0x{v:X16}{(externalOnly[i] != 0 ? "(external_only)" : string.Empty)}"));
        _logger?.LogInformation(
            "[OPENGL-ZEROCOPY] 显示 fourcc=0x{Fourcc:X8} 支持的 modifier：{List}",
            drmFourcc,
            list);
    }

    /// <summary>构造并创建单平面 dma_buf EGLImage（R8 / GR88）；失败返回 Zero（eglGetError 可查）。</summary>
    private static unsafe nint CreateDmaBufPlaneImage(
        nint display, int fd, int width, int height, uint offset, uint pitch, int drmFourcc, bool withModifier, ulong modifier)
    {
        var attribs = BuildDmaBufPlaneAttribs(fd, width, height, offset, pitch, drmFourcc, withModifier, modifier);
        fixed (int* p = attribs)
        {
            // dma_buf 导入：ctx 与 clientBuffer 均须 EGL_NO_*（零）——五参签名缺一即实参错位（历史全败根因）。
            return EglDmaBufImport.EglCreateImageKHR(display, nint.Zero, (uint)EglDmaBufImport.EglLinuxDmaBufExt, nint.Zero, p);
        }
    }

    /// <summary>构造单平面 dma_buf EGLImage 属性表（R8 / GR88 等单平面格式）。</summary>
    /// <remarks>平面数由出现的最高平面号属性推断（EGL 无 PLANE_COUNT 键），单平面表止于 PLANE0_*。</remarks>
    private static int[] BuildDmaBufPlaneAttribs(
        int fd, int width, int height, uint offset, uint pitch, int drmFourcc, bool hasModifier, ulong modifier)
    {
        if (hasModifier)
        {
            return new[]
            {
                EglDmaBufImport.EglWidth, width,
                EglDmaBufImport.EglHeight, height,
                EglDmaBufImport.EglDmaBufPlane0FdExt, fd,
                EglDmaBufImport.EglDmaBufPlane0OffsetExt, (int)offset,
                EglDmaBufImport.EglDmaBufPlane0PitchExt, (int)pitch,
                EglDmaBufImport.EglDmaBufPlane0ModifierLoExt, (int)(modifier & 0xFFFFFFFF),
                EglDmaBufImport.EglDmaBufPlane0ModifierHiExt, (int)(modifier >> 32),
                EglDmaBufImport.EglLinuxDrmFourccExt, drmFourcc,
                EglDmaBufImport.EglNone,
            };
        }
        return new[]
        {
            EglDmaBufImport.EglWidth, width,
            EglDmaBufImport.EglHeight, height,
            EglDmaBufImport.EglDmaBufPlane0FdExt, fd,
            EglDmaBufImport.EglDmaBufPlane0OffsetExt, (int)offset,
            EglDmaBufImport.EglDmaBufPlane0PitchExt, (int)pitch,
            EglDmaBufImport.EglLinuxDrmFourccExt, drmFourcc,
            EglDmaBufImport.EglNone,
        };
    }

    /// <summary>关闭 Linux dma_buf 文件描述符（导入完成后由导入方负责关闭，防 fd 泄漏）。</summary>
    [LibraryImport("libc")]
    private static partial int close(int fd);

    private static void CloseFd(int fd)
    {
        if (fd >= 0) _ = close(fd);
    }

    // 单平面 R8 / 双通道 GR88 EGLImage 纹理须显式设过滤/环绕参数：默认 MIN_FILTER 为 NEAREST_MIPMAP_LINEAR
    // 且无 mipmap → 纹理「不完整」，采样恒返回 (0,0,0,1)。必须置 LINEAR + CLAMP_TO_EDGE 才能正常采样。
    private const int GlTexMinFilter = 0x2801;
    private const int GlTexMagFilter = 0x2800;
    private const int GlTexWrapS = 0x2802;
    private const int GlTexWrapT = 0x2803;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;

    private static void SetDmaBufTexParams()
    {
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexMinFilter, GlLinear);
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexMagFilter, GlLinear);
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexWrapS, GlClampToEdge);
        GLNative.glTexParameteri((uint)GLNative.GlTexture2DConst, (uint)GlTexWrapT, GlClampToEdge);
    }
}