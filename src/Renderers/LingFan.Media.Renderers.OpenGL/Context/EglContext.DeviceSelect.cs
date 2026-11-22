using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL.Context;

// EglContext 的 Linux 硬件设备选择职责段（部分类，按职责拆分，编译期等价）。
// EGL_EXT_platform_device 设备枚举与 DRM 渲染节点/sysfs vendor 判据（同设备零拷贝前提）；
// 上下文创建与 MakeCurrent 在主文件。
internal sealed unsafe partial class EglContext : IGlContext
{
    /// <summary>EGL 设备的 DRM 渲染节点路径（EGL_DRM_RENDER_NODE_FILE_EXT = 0x3234，需 EGL_EXT_device_drm_render_node）；不支持返回 null。</summary>
    private static unsafe string? QueryDrmRenderNode(nint dev)
    {
        const int EglDrmRenderNodeFileExt = 0x3234;
        nint nodePtr = EglNative.eglQueryDeviceStringEXT(dev, EglDrmRenderNodeFileExt);
        return nodePtr != nint.Zero
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(nodePtr)
            : null;
    }

    /// <summary>读 DRM 节点的 sysfs PCI vendor（/sys/class/drm/&lt;node&gt;/device/vendor，0x8086=Intel / 0x10DE=NVIDIA / 0x1002=AMD）；不可读返回 null。</summary>
    private static string? QuerySysfsVendor(string nodePath)
    {
        try
        {
            string name = global::System.IO.Path.GetFileName(nodePath);
            return global::System.IO.File.ReadAllText($"/sys/class/drm/{name}/device/vendor").Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Linux：经 EGL_EXT_platform_device 选择硬件 EGL 设备（跳过 EGL_MESA_device_software 软件设备），
    /// 返回已 Initialize 的 EGLDisplay。任一环节失败返回 Zero（调用方回落默认显示）。
    /// 用途：同设备零拷贝——离屏上下文与 VAAPI 解码（Intel iHD）同 GPU 时 dma_buf 导入才可用；
    /// 默认显示在 Xvfb 下落到 llvmpipe 软件栈，无法导入 GPU dma_buf。
    /// <para>全链路逐步打点（设备数/客户端扩展/逐设备扩展/平台显示/初始化）——设备选择属静默失败
    /// 高发区，无打点即不可诊断。</para>
    /// </summary>
    private static unsafe nint TryOpenHardwareDeviceDisplay(ILogger? logger)
    {
        try
        {
            // 客户端扩展自报（EGL_NO_DISPLAY 查询）：设备枚举/平台设备能力缺一即无选择资格。
            nint clientExts = EglNative.eglQueryString(nint.Zero, EglDeviceExtensions);
            string clientExtStr = clientExts != nint.Zero
                ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(clientExts) ?? string.Empty
                : string.Empty;
            bool hasEnum = clientExtStr.Contains("EGL_EXT_device_enumeration", StringComparison.Ordinal);
            bool hasPlatform = clientExtStr.Contains("EGL_EXT_platform_device", StringComparison.Ordinal);
            logger?.LogInformation(
                "[EGL-DEVICE] 客户端扩展：device_enumeration={Enum} platform_device={Platform}",
                hasEnum, hasPlatform);
            if (!hasEnum || !hasPlatform)
            {
                logger?.LogWarning("[EGL-DEVICE] 客户端不支持 EGL 设备枚举/平台设备，回落默认显示。");
                return nint.Zero;
            }

            int count = 0;
            if (EglNative.eglQueryDevicesEXT(0, null, &count) == 0 || count == 0)
            {
                logger?.LogWarning(
                    "[EGL-DEVICE] eglQueryDevicesEXT 计数查询失败（设备数={Count}，eglErr=0x{Err:X8}），回落默认显示。",
                    count, EglNative.eglGetError());
                return nint.Zero;
            }

            var devices = new nint[count];
            fixed (nint* d = devices)
            {
                if (EglNative.eglQueryDevicesEXT(count, d, &count) == 0)
                {
                    logger?.LogWarning(
                        "[EGL-DEVICE] eglQueryDevicesEXT 枚举失败（eglErr=0x{Err:X8}），回落默认显示。",
                        EglNative.eglGetError());
                    return nint.Zero;
                }
            }

            // 设备选择判据（治原生崩溃）：VAAPI 解码 GPU（Intel iHD）与 EGL 显示必须同设备——跨厂商
            // dma_buf 导入已证不可行，且 NVIDIA 驱动对跨厂商导入直接原生崩溃（复测 3 实证）。
            // GLVND 枚举混排多厂商 EGL 设备（NVIDIA 专有设备可排第一），"首个非软件设备"启发式不可用。
            // 判据链：EGL 设备 → DRM 渲染节点（EGL_EXT_device_drm_render_node）→ sysfs vendor
            // （/sys/class/drm/<node>/device/vendor，0x8086=Intel）；自动模式优先 Intel，
            // LINGFAN_EGL_DEVICE_DRM=renderD129 可显式指定节点（语义自"1"=自动扩展）。
            string envVal = Environment.GetEnvironmentVariable("LINGFAN_EGL_DEVICE_DRM") ?? "1";
            bool explicitNode = envVal != "1" && envVal.Length > 0;

            for (int i = 0; i < devices.Length; i++)
            {
                nint dev = devices[i];
                if (dev == nint.Zero) continue;
                nint extPtr = EglNative.eglQueryDeviceStringEXT(dev, EglDeviceExtensions);
                string exts = extPtr != nint.Zero
                    ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(extPtr) ?? string.Empty
                    : string.Empty;
                string? node = QueryDrmRenderNode(dev);
                string? vendor = node is null ? null : QuerySysfsVendor(node);
                logger?.LogInformation(
                    "[EGL-DEVICE] 设备#{Idx} node={Node} vendor={Vendor} extensions={Exts}",
                    i, node ?? "无", vendor ?? "未知", exts.Length > 120 ? exts[..120] + "…" : exts);

                if (exts.Contains("EGL_MESA_device_software", StringComparison.Ordinal))
                    continue;   // 软件设备（llvmpipe）：无法导入 GPU dma_buf，跳过

                // NVIDIA 专有 EGL 设备（EGL_NV_device_cuda）：跨厂商导入不可行且存在原生崩溃风险，跳过。
                if (exts.Contains("EGL_NV_device_cuda", StringComparison.Ordinal))
                {
                    logger?.LogInformation("[EGL-DEVICE] 设备#{Idx} 为 NVIDIA 专有 EGL 设备，跳过（跨厂商导入不可行）。", i);
                    continue;
                }

                // 显式节点匹配（LINGFAN_EGL_DEVICE_DRM=renderD129）：只取指定节点。
                if (explicitNode && (node is null || !node.EndsWith(envVal, StringComparison.Ordinal)))
                {
                    logger?.LogInformation("[EGL-DEVICE] 设备#{Idx} 非指定节点（要求 {Want}），跳过。", i, envVal);
                    continue;
                }

                // 自动模式优先 Intel（0x8086）；vendor 可读且非 Intel 的设备跳过。
                if (!explicitNode && vendor is not null && vendor != "0x8086")
                {
                    logger?.LogInformation(
                        "[EGL-DEVICE] 设备#{Idx} vendor={Vendor} 非 Intel，跳过（跨厂商导入不可行）。", i, vendor);
                    continue;
                }

                nint disp = EglNative.eglGetPlatformDisplayEXT(EglPlatformDeviceExt, dev, null);
                if (disp == nint.Zero)
                {
                    logger?.LogWarning(
                        "[EGL-DEVICE] 设备#{Idx} eglGetPlatformDisplayEXT 失败（eglErr=0x{Err:X8}），尝试下一设备。",
                        i, EglNative.eglGetError());
                    continue;
                }

                int major = 0, minor = 0;
                if (EglNative.eglInitialize(disp, &major, &minor) != 0)
                {
                    logger?.LogInformation(
                        "[EGL-DEVICE] 已选择硬件设备 #{Idx}（EGL {Major}.{Minor}）。", i, major, minor);
                    return disp;
                }
                logger?.LogWarning(
                    "[EGL-DEVICE] 设备#{Idx} eglInitialize 失败（eglErr=0x{Err:X8}），尝试下一设备。",
                    i, EglNative.eglGetError());
                EglNative.eglTerminate(disp);   // 初始化失败：换下一个设备
            }

            logger?.LogWarning("[EGL-DEVICE] 无可用硬件 EGL 设备（全部跳过/失败），回落默认显示。");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[EGL-DEVICE] 设备选择异常，回落默认显示。");
        }
        return nint.Zero;
    }
}
