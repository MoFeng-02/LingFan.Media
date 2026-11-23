using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LingFan.Media.Abstractions;

namespace MediaCorrectnessProbe;

// ImageUtil 的音频/PNG 编码段（部分类，由 ImageUtil.cs 按职责拆分，编译期等价）。
internal static partial class ImageUtil
{
    /// <summary>把任意格式 PCM 混成单声道浮点（±1.0）追加到累加缓冲。</summary>
    internal static void AppendMono(ReadOnlySpan<byte> pcm, SampleFormat fmt, int channels,
        float[] acc, ref int accLen, int maxLen)
    {
        int bps = fmt switch { SampleFormat.S16 => 2, _ => 4 };
        int frames = pcm.Length / (bps * channels);
        for (int i = 0; i < frames && accLen < maxLen; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int off = (i * channels + c) * bps;
                sum += bps switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(pcm[off..]) / 32768f,
                    4 when fmt == SampleFormat.F32 => BinaryPrimitives.ReadSingleLittleEndian(pcm[off..]),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(pcm[off..]) / 2147483648f
                };
            }
            acc[accLen++] = sum / channels;
        }
    }

    /// <summary>把原始 PCM 写成标准 WAV（S16→PCM(1)，F32→IEEE_FLOAT(3)）。</summary>
    internal static void WriteWav(string path, byte[] pcm, int sampleRate, int channels, SampleFormat fmt)
    {
        int bps = fmt == SampleFormat.S16 ? 2 : 4;
        short audioFormat = fmt == SampleFormat.F32 ? (short)3 : (short)1;
        int blockAlign = channels * bps;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write(audioFormat); w.Write((short)channels); w.Write(sampleRate);
        w.Write(sampleRate * blockAlign); w.Write((short)blockAlign); w.Write((short)(bps * 8));
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
    }

    /// <summary>极简 PNG 编码器（RGBA / color type 6，zlib 走 BCL ZLibStream，无第三方依赖）。</summary>
    internal static class Png
    {
        private static readonly uint[] CrcTable = BuildCrc();

        internal static void EncodeRgba(string path, int w, int h, ReadOnlySpan<byte> rgba)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);

            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
            ihdr[8] = 0x08; ihdr[9] = 0x06;
            WriteChunk(fs, "IHDR", ihdr);

            using var ms = new MemoryStream();
            using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[w * 4 + 1];
                row[0] = 0;
                for (int y = 0; y < h; y++)
                {
                    rgba.Slice(y * w * 4, w * 4).CopyTo(row.AsSpan(1));
                    zs.Write(row);
                }
            }
            WriteChunk(fs, "IDAT", ms.ToArray());
            WriteChunk(fs, "IEND", []);
        }

        private static void WriteChunk(Stream fs, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            fs.Write(len);
            fs.Write(typeBytes);
            fs.Write(data);
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc(typeBytes, data));
            fs.Write(crcBuf);
        }

        private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte x in a) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
            foreach (byte x in b) crc = (crc >> 8) ^ CrcTable[(crc ^ x) & 0xFF];
            return crc ^ 0xFFFFFFFF;
        }

        private static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
    }
}
