using System;

namespace LingFan.Media.Backends.VulkanVideo.H264;

/// <summary>
/// H.264 原始字节流（RBSP）位读取器：Exp-Golomb (ue/se) + 定长位。
/// </summary>
/// <remarks>
/// <para>仅用于把 SPS/PPS/Slice 头解析为 Vulkan STD 结构，零反射、无原生依赖。</para>
/// <para>AOT 兼容：纯托管只读结构，无 unsafe（按位访问 ReadOnlySpan）。</para>
/// </remarks>
internal ref struct H264BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public H264BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPos = 0;
    }

    public bool Eof => _bitPos >= _data.Length * 8;

    public int ReadBit()
    {
        if (Eof) return 0;
        int bytePos = _bitPos >> 3;
        int bitInByte = 7 - (_bitPos & 7);
        _bitPos++;
        return (_data[bytePos] >> bitInByte) & 1;
    }

    public uint ReadBits(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++)
            v = (v << 1) | (uint)ReadBit();
        return v;
    }

    /// <summary>无符号 Exp-Golomb：ue(v)。</summary>
    public int ReadUe()
    {
        int leadingZeros = 0;
        while (ReadBit() == 0)
        {
            if (++leadingZeros > 31) return 0; // 防越界/EOF 死循环
        }

        if (leadingZeros == 0) return 0;
        uint val = ReadBits(leadingZeros);
        return (int)((1u << leadingZeros) - 1 + val);
    }

    /// <summary>有符号 Exp-Golomb：se(v)。</summary>
    public int ReadSe()
    {
        int k = ReadUe();
        if (k == 0) return 0;
        bool negative = (k & 1) != 0;
        int magnitude = (k + 1) >> 1;
        return negative ? -magnitude : magnitude;
    }
}
