using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Platforms.Linux;

/// <summary>
/// VA-API（Video Acceleration API）硬件解码互操作（Linux 真实实现）。
/// </summary>
/// <remarks>
/// <para>职责：把 FFmpeg VAAPI 硬解产出的 VA Surface 经 libva
/// <c>vaExportSurfaceHandle(DRM_PRIME_2)</c> 导出为 dma_buf fd + 多平面布局，供 GL/Vulkan 零拷贝导入上屏。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VAAPI → VA Surface → dma_buf(DRM_PRIME_2) → GL/VK 纹理 → 渲染器上屏（无 CPU 回读）。</para>
/// <para><b>实现</b>：libva 原生 P/Invoke（<c>libva.so.2</c>），[LibraryImport] 源生成、零反射、AOT 友好；
/// 调用约定默认 Winapi（x64 下与 libva 的 C 调用约定等价）。</para>
/// <para>VA-API 硬解为 Linux（Intel/AMD GPU + mesa + libva）真实零拷贝路径，非 Phase 2 桩。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——原生 VA-API 调用是同步边界，无 I/O。</para>
/// </remarks>
public sealed partial class VaApiInterop : IVaApiExport
{
    // libva 常量（va/va.h、va/va_drm.h）
    private const uint VA_STATUS_SUCCESS = 0;
    private const uint VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2 = 0x40000000;
    private const uint VA_EXPORT_SURFACE_READ_ONLY = 0x0001;
    private const uint VA_EXPORT_SURFACE_COMPOSED_LAYERS = 0x0008; // NV12 合成单层双平面（单 fd）

    /// <inheritdoc/>
    public unsafe bool TryExportSurfaceToDmaBuf(nint vaDisplay, uint surfaceId, out VaApiDmaBufDescriptor? descriptor)
    {
        descriptor = null;
        if (vaDisplay == nint.Zero) return false;

        // 清零描述符（libva 负责填充 num_objects/num_layers 与各层 num_planes）。
        var desc = new VADRMPRIMESurfaceDescriptor();

        uint status = vaExportSurfaceHandle(
            vaDisplay, surfaceId,
            VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2,
            VA_EXPORT_SURFACE_READ_ONLY | VA_EXPORT_SURFACE_COMPOSED_LAYERS,
            ref desc);
        if (status != VA_STATUS_SUCCESS)
            return false;

        int objCount = (int)Math.Min(desc.num_objects, 4);
        if (objCount <= 0) return false;

        var fds = new int[objCount];
        for (int i = 0; i < objCount; i++)
            fds[i] = desc.objects[i].fd;
        // composed：所有平面共享对象 0 的修饰符
        ulong modifier = desc.objects[0].drm_format_modifier;

        int layerCount = (int)Math.Min(desc.num_layers, 4);
        var layer = desc.layers[0]; // composed NV12：单层含 Y/UV 双平面
        int planeCount = (int)Math.Min(layer.num_planes, 4);
        if (planeCount <= 0) return false;

        var planeObj = new uint[planeCount];
        var planeOff = new uint[planeCount];
        var planePitch = new uint[planeCount];
        for (int p = 0; p < planeCount; p++)
        {
            planeObj[p] = layer.object_index[p];
            planeOff[p] = layer.offset[p];
            planePitch[p] = layer.pitch[p];
        }

        descriptor = new VaApiDmaBufDescriptor
        {
            Width = (int)desc.width,
            Height = (int)desc.height,
            DrmFourcc = desc.fourcc,
            Modifier = modifier,
            ObjectCount = objCount,
            ObjectFds = fds,
            LayerCount = layerCount,
            PlaneObjectIndices = planeObj,
            PlaneOffsets = planeOff,
            PlanePitches = planePitch,
        };
        return true;
    }

    [LibraryImport("libva.so.2", EntryPoint = "vaExportSurfaceHandle")]
    private static partial uint vaExportSurfaceHandle(
        nint dpy,
        uint surface,
        uint memType,
        uint flags,
        ref VADRMPRIMESurfaceDescriptor descriptor);

    // ── libva 原生结构（VADRMPRIMESurfaceDescriptor，va/va_drm.h）──
    // 固定大小内联数组经 [InlineArray] 表达，使整体成为 blittable 布局（与 libva C 逐字节一致）；
    // [LibraryImport] 源生成器对 blittable 结构按指针直传、零封送（AOT 友好，无 SYSLIB1051）。

    [StructLayout(LayoutKind.Sequential)]
    private struct VA_DRMPRIME_Object
    {
        public int fd;
        public uint size;
        public ulong drm_format_modifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VA_DRMPRIME_Layer
    {
        public uint drm_format;
        public uint num_planes;
        public Uint4 object_index;
        public Uint4 offset;
        public Uint4 pitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VADRMPRIMESurfaceDescriptor
    {
        public uint fourcc;
        public uint width;
        public uint height;
        public uint num_objects;
        public ObjectArray objects;
        public uint num_layers;
        public LayerArray layers;
    }

    [InlineArray(4)]
    private struct ObjectArray
    {
        public VA_DRMPRIME_Object Element;
    }

    [InlineArray(4)]
    private struct LayerArray
    {
        public VA_DRMPRIME_Layer Element;
    }

    [InlineArray(4)]
    private struct Uint4
    {
        public uint Element;
    }
}
