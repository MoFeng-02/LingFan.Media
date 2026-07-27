namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// MediaFoundation 常量与 GUID 定义。
/// </summary>
/// <remarks>
/// 仅 Windows 可用。非 Windows 平台不加载此模块（编译安全，运行时由 MFBackend 检测平台）。
/// </remarks>
internal static class MFConstants
{
    // MF_VERSION
    internal const int MF_VERSION = 0x00020070; // MF_API_VERSION_0x70 = Windows 10+

    // MFStartup flags
    internal const int MFSTARTUP_LITE = 0x00000001;
    internal const int MFSTARTUP_NOSOCKET = 0x00000002;
    internal const int MFSTARTUP_FULL = 0x00000000;

    // MF_SOURCE_READER_FLAG
    internal const int MF_SOURCE_READERF_ERROR = 0x00000001;
    internal const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    internal const int MF_SOURCE_READERF_NEWSTREAM = 0x00000004;
    internal const int MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED = 0x00000010;
    internal const int MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;
    internal const int MF_SOURCE_READERF_STREAMTICK = unchecked((int)0x00000100);

    // MF_SOURCE_READER_CONTROL_FLAG
    internal const int MF_SOURCE_READER_CONTROLF_DRAIN = 0x00000001;

    // Stream index
    internal const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0;
    internal const int MF_SOURCE_READER_FIRST_AUDIO_STREAM = 1;
    internal const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFF;

    // MF_MT_* attribute IDs (subset)
    internal static readonly Guid MF_MT_MAJOR_TYPE = new(0x48eba28e, 0x6b9e, 0x410f, 0x80, 0x95, 0x4a, 0xf5, 0x97, 0x3d, 0x6f, 0x2);
    internal static readonly Guid MF_MT_SUBTYPE = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    internal static readonly Guid MF_MT_FRAME_SIZE = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    internal static readonly Guid MF_MT_FRAME_RATE = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xbc, 0xc8, 0x12, 0x35, 0x7d, 0x11, 0x47, 0x21);
    internal static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new(0x5faeeae7, 0x0290, 0x4c31, 0x9e, 0x9a, 0xc9, 0x69, 0xd5, 0x5d, 0x01, 0x09);
    internal static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new(0x37e48bf5, 0x645e, 0x4c5b, 0x89, 0xde, 0xad, 0xa9, 0xe2, 0xb7, 0x2d, 0x77);
    internal static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new(0xf2deb4fb, 0x4c2a, 0x4dac, 0xb4, 0xbd, 0x61, 0x6f, 0x61, 0x5e, 0x0a, 0xc2);

    // Major type GUIDs
    internal static readonly Guid MFMediaType_Video = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid MFMediaType_Audio = new(0x73647561, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // Video subtype GUIDs
    internal static readonly Guid MFVideoFormat_H264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "H264"
    internal static readonly Guid MFVideoFormat_H265 = new(0x35363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "H265"
    internal static readonly Guid MFVideoFormat_NV12 = new(0x3231564e, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "NV12"
    internal static readonly Guid MFVideoFormat_RGB32 = new(0x00000016, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // Audio subtype GUIDs
    internal static readonly Guid MFAudioFormat_PCM = new(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid MFAudioFormat_AAC = new(0x00001610, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid MFAudioFormat_MP3 = new(0x00000055, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // HRESULT
    internal const int S_OK = 0;
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC01D4005L);
    internal const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    internal const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D71);
}
