namespace LingFan.Media.Video.Processors;

/// <summary>
/// 视频处理器内部共享工具（帧格式判定）。不对外暴露。
/// </summary>
internal static class FrameUtil
{
    /// <summary>返回像素格式的每像素字节数；不支持的格式返回 0。</summary>
    public static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.BGRA32 => 4,
        PixelFormat.RGBA32 => 4,
        PixelFormat.RGB24 => 3,
        _ => 0,
    };

    /// <summary>
    /// 判定帧是否为可软件处理的打包（packed）CPU 帧（BGRA32/RGBA32/RGB24）。
    /// 平面/半平面（YUV*/NV12/NV21）与 GPU 资源返回 false，交由调用方透传。
    /// </summary>
    public static bool TryGetPackedSoftware(VideoFrame frame, out SoftwareFrameResource resource, out int bpp)
    {
        if (frame.Resource is SoftwareFrameResource s)
        {
            int b = BytesPerPixel(s.Format);
            if (b > 0)
            {
                resource = s;
                bpp = b;
                return true;
            }
        }
        resource = null!;
        bpp = 0;
        return false;
    }
}
