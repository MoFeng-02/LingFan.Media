using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace LingFan.Media.Backends.VLC.Demuxer;

/// <summary>
/// 将 <see cref="IMediaStream"/> 适配为 VLC 的 <see cref="MediaInput"/>。
/// </summary>
/// <remarks>
/// <para>VLC 的 MediaInput 需要同步 Read/Seek，对应 <see cref="IMediaStream"/> 的同步方法。</para>
/// <para>网络流建连已在 <see cref="VLCDemuxer.OpenAsync"/> 的异步路径完成（ConnectAsync），
/// 此处仅做已连接流的逐块同步读取——与 FFmpeg AVIO 回调同属同步边界。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class MediaStreamInput : MediaInput
{
    private readonly IMediaStream _stream;

    /// <summary>
    /// 初始化 <see cref="MediaStreamInput"/> 的新实例。
    /// </summary>
    /// <param name="stream">媒体数据流（必须已建连）。</param>
    public MediaStreamInput(IMediaStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// 打开流。VLC 在创建 Media 时调用。
    /// </summary>
    public override bool Open(out ulong size)
    {
        long len = _stream.Length;
        size = len < 0 ? 0 : (ulong)len;
        return true;
    }

    /// <summary>
    /// 同步读取数据到 VLC 提供的 native buffer。
    /// </summary>
    /// <remarks>同步边界：VLC 的 MediaInput.Read 是 C 回调签名，强制同步。</remarks>
    public override int Read(IntPtr buf, uint size)
    {
        if (size == 0) return 0;

        byte[] buffer = new byte[size];
        int read = _stream.Read(buffer.AsSpan(0, (int)size));

        if (read > 0)
        {
            Marshal.Copy(buffer, 0, buf, read);
        }

        return read;
    }

    /// <summary>
    /// 定位流位置（绝对位置）。
    /// </summary>
    /// <returns>成功返回 true，失败或不可定位返回 false。</returns>
    public override bool Seek(ulong pos)
    {
        if (!_stream.CanSeek) return false;

        long result = _stream.Seek((long)pos, SeekOrigin.Begin);
        return result >= 0;
    }

    /// <summary>
    /// 关闭流。
    /// </summary>
    public override void Close()
    {
        _stream.Close();
    }
}
