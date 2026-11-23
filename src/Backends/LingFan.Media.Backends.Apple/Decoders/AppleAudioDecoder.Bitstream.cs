namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// 码流头解析分部：AAC AudioSpecificConfig 与 MP3 帧头的采样率/声道解析（纯位流解析，零原生调用）。
/// </summary>
internal sealed unsafe partial class AppleAudioDecoder
{
    // AAC AudioSpecificConfig（ISO/IEC 14496-3 §1.6.2.1）解析
    private static readonly int[] AacSampleRates =
        { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

    private static bool TryParseAacAudioSpecificConfig(ReadOnlyMemory<byte> asc, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        var s = asc.Span;
        if (s.Length < 2) return false;

        int b0 = s[0];
        int b1 = s[1];
        int srIndex = ((b0 & 0x07) << 1) | (b1 >> 7);
        int chConfig = (b1 >> 3) & 0x0F;
        if (srIndex >= AacSampleRates.Length) return false;

        sampleRate = AacSampleRates[srIndex];
        channels = chConfig switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 5,
            6 => 6,
            7 => 8,
            11 => 7,
            _ => 0,
        };
        return sampleRate > 0 && channels > 0;
    }

    // MP3 帧头（MPEG1 Layer III 为主）解析，用于懒回填 Output*
    private static bool TryParseMp3FrameHeader(ReadOnlyMemory<byte> data, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        var s = data.Span;
        if (s.Length < 4) return false;
        if (s[0] != 0xFF || (s[1] & 0xE0) != 0xE0) return false;

        int version = (s[1] >> 3) & 0x03;   // 11=MPEG1, 10=MPEG2, 00=MPEG2.5
        int srIndex = (s[2] >> 2) & 0x03;
        int chMode = (s[3] >> 6) & 0x03;      // 11=单声道

        int ver = version switch
        {
            3 => 1,   // MPEG1
            2 => 2,   // MPEG2
            _ => 25,  // MPEG2.5
        };
        int[] sr1 = { 44100, 48000, 32000, 0 };
        int[] sr2 = { 22050, 24000, 16000, 0 };
        sampleRate = ver == 1 ? sr1[srIndex] : sr2[srIndex];
        channels = chMode == 3 ? 1 : 2;
        return sampleRate > 0;
    }
}