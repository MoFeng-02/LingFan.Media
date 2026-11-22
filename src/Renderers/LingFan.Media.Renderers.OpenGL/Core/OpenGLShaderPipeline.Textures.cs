using System.Runtime.InteropServices;
using System.Text;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

// OpenGLShaderPipeline 的 Textures 职责段（部分类，由 OpenGLShaderPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/EnsureInitialized/Dispose）在主文件；全部同步原生调用。
internal sealed unsafe partial class OpenGLShaderPipeline
{
    private void EnsureTextures(int width, int height, PixelFormat format)
    {
        if (_cachedWidth == width && _cachedHeight == height &&
            _cachedFormat == format && _planeTextures[0] != 0)
            return;

        ReleaseTextures();

        switch (format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
                CreatePlane(0, width, height, GlRgba8, GlRgba);
                break;

            case PixelFormat.RGB24:
                CreatePlane(0, width, height, GlRgb8, GlRgb);
                break;

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
            {
                (int cw, int ch) = format switch
                {
                    PixelFormat.YUV444P => (width, height),
                    PixelFormat.YUV422P => ((width + 1) / 2, height),
                    _ => ((width + 1) / 2, (height + 1) / 2),
                };
                CreatePlane(0, width, height, GlR8, GlRed);
                CreatePlane(1, cw, ch, GlR8, GlRed);
                CreatePlane(2, cw, ch, GlR8, GlRed);
                break;
            }

            case PixelFormat.NV12:
            case PixelFormat.NV21:
            {
                int uvW = (width + 1) / 2;
                int uvH = (height + 1) / 2;
                CreatePlane(0, width, height, GlR8, GlRed);
                CreatePlane(1, uvW, uvH, GlRg8, GlRg);
                break;
            }

            default:
                throw new NotSupportedException($"OpenGL Shader 管线不支持像素格式 {format}。");
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedFormat = format;
    }

    private void CreatePlane(int index, int width, int height, int internalFormat, int format)
    {
        fixed (uint* p = &_planeTextures[index])
            GLNative.glGenTextures(1, p);
        uint tex = _planeTextures[index];
        GLNative.glBindTexture(GlTexture2D, tex);
        GLNative.glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        GLNative.glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        GLNative.glTexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        GLNative.glTexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        // 仅分配（数据由 UploadPlanes 经 TexSubImage 填充）
        GLNative.glTexImage2D(GlTexture2D, 0, internalFormat, width, height, 0, (uint)format, GlUnsignedByte, null);
    }

    private static int BppToFormat(int bpp) => bpp switch
    {
        1 => GlRed,
        2 => GlRg,
        3 => GlRgb,
        _ => GlRgba,
    };

    private void UploadPlanes(SoftwareFrameResource sw)
    {
        GLNative.glPixelStorei(GlUnpackAlignment, 1); // 单通道平面逐行按实际宽度读取，避免 4 字节对齐错位
        var span = sw.Data.Span;
        fixed (byte* basePtr = span)
        {
            int w = sw.Width, h = sw.Height;
            switch (sw.Format)
            {
                case PixelFormat.BGRA32:
                case PixelFormat.RGBA32:
                {
                    int bpp = 4;
                    int stride = sw.Stride > 0 ? sw.Stride : w * bpp;
                    UploadPlane(_planeTextures[0], w, h, bpp, stride, GlRgba, basePtr);
                    break;
                }

                case PixelFormat.RGB24:
                {
                    int bpp = 3;
                    int stride = sw.Stride > 0 ? sw.Stride : w * bpp;
                    UploadPlane(_planeTextures[0], w, h, bpp, stride, GlRgb, basePtr);
                    break;
                }

                case PixelFormat.YUV420P:
                case PixelFormat.YUV422P:
                case PixelFormat.YUV444P:
                {
                    (int cw, int ch) = sw.Format switch
                    {
                        PixelFormat.YUV444P => (w, h),
                        PixelFormat.YUV422P => ((w + 1) / 2, h),
                        _ => ((w + 1) / 2, (h + 1) / 2),
                    };
                    int ySize = w * h;
                    int cSize = cw * ch;
                    UploadPlane(_planeTextures[0], w, h, 1, w, GlRed, basePtr);
                    UploadPlane(_planeTextures[1], cw, ch, 1, cw, GlRed, basePtr + ySize);
                    UploadPlane(_planeTextures[2], cw, ch, 1, cw, GlRed, basePtr + ySize + cSize);
                    break;
                }

                case PixelFormat.NV12:
                case PixelFormat.NV21:
                {
                    int ySize = w * h;
                    int uvW = (w + 1) / 2;
                    int uvH = (h + 1) / 2;
                    UploadPlane(_planeTextures[0], w, h, 1, w, GlRed, basePtr);
                    UploadPlane(_planeTextures[1], uvW, uvH, 2, uvW * 2, GlRg, basePtr + ySize);
                    break;
                }

                default:
                    throw new NotSupportedException($"OpenGL Shader 管线不支持像素格式 {sw.Format}。");
            }
        }
    }

    private static void UploadPlane(uint tex, int w, int h, int bpp, int stride, int format, byte* data)
    {
        GLNative.glBindTexture(GlTexture2D, tex);
        int glFormat = BppToFormat(bpp);
        if (stride == w * bpp || stride == 0)
        {
            GLNative.glTexSubImage2D(GlTexture2D, 0, 0, 0, w, h, (uint)glFormat, GlUnsignedByte, data);
        }
        else
        {
            // 零拷贝对齐 stride（Stride > w*bpp）：逐行上传
            for (int y = 0; y < h; y++)
                GLNative.glTexSubImage2D(GlTexture2D, 0, 0, y, w, 1, (uint)glFormat, GlUnsignedByte, data + y * stride);
        }
    }

    // 对外呈现

    private void ReleaseTextures()
    {
        for (int i = 0; i < _planeTextures.Length; i++)
        {
            if (_planeTextures[i] != 0)
            {
                fixed (uint* p = &_planeTextures[i])
                    GLNative.glDeleteTextures(1, p);
                _planeTextures[i] = 0;
            }
        }
        _cachedWidth = 0;
        _cachedHeight = 0;
        _cachedFormat = (PixelFormat)(-1);
    }
}
