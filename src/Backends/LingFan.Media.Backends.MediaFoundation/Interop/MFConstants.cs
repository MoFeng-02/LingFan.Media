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

    // MF_SOURCE_READER_CONSTANTS（mfreadwrite.h 权威值）——
    // ⚠️ 早期误写 0 / 1 / 0xFFFFFFFF（把「首个视频/音频流」当成了字面流索引），与 SDK 语义完全不同。
    // 本 Demuxer 走逐流枚举 + 真实索引，不依赖这些伪流标识；此处仅保留 SDK 正确值以防后续误用。
    internal const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    internal const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    internal const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    internal const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;

    // MF_PD_DURATION {8C1C9CF8-DEE1-4BFC-8C3F-4F8C7C2711AB}（mfidl.h 权威值）：
    // presentation descriptor 时长属性（UINT64，100ns 单位）。MF 不会自动填充时长，
    // 须由 IMFSourceReader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION) 取得。
    // ⚠️ 早期 MFDemuxer 未查此属性、把 Duration 硬编码 TimeSpan.Zero，导致 player.Duration 恒为 0，
    //    完整播放测试首轮即满足「pos >= duration-1」假完成（表现「几秒播完 21 秒视频」）。勿回退为 0。
    internal static readonly Guid MF_PD_DURATION = new(0x8c1c9cf8, 0xdee1, 0x4bfc, 0x8c, 0x3f, 0x4f, 0x8c, 0x7c, 0x27, 0x11, 0xab);

    // MF_MT_* attribute IDs (subset)
    internal static readonly Guid MF_MT_MAJOR_TYPE = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f); // {48EBA18E-F8C9-4687-BF11-0A74C9F96A8F} 已本机 SetInputType 运行时验证
    internal static readonly Guid MF_MT_SUBTYPE = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    // H264/H265 解码必需：out-of-band SPS+PPS（Annex-B 序列头），设到输入媒体类型。
    // mfapi.h 权威值 {3C036DE7-3AD0-4C9E-9216-EE6D6AC21CB3}（2026-07-31 比对 Windows SDK 10.0.26100.0 头文件原文）。
    // ⚠️ 早期误写 C8BF26B0-...（臆测值）：SetBlob 写到了一个 MFT 完全不认识的键 → 序列头形同虚设，
    //    且 TryGetBlob 恒 MF_E_ATTRIBUTENOTFOUND，由此得出的「MF 媒体源不填此属性」结论无效，勿再引用。
    internal static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new(0x3c036de7, 0x3ad0, 0x4c9e, 0x92, 0x16, 0xee, 0x6d, 0x6a, 0xc2, 0x1c, 0xb3);

    /// <summary>MF_MT_MINIMUM_DISPLAY_APERTURE {D7388766-18FE-48C6-A177-EE894867C8C4}（mfapi.h）：
    /// Blob = MFVideoArea（16 字节：MFOffset OffsetX(4) + MFOffset OffsetY(4) + SIZE Area(8)）。
    /// H264 解码输出为宏块对齐编码尺寸（如 1920x1088），显示尺寸（1920x1080）从该属性取。
    /// 已本机运行时验证：解出 Area=1920x1080。</summary>
    internal static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new(0xd7388766, 0x18fe, 0x48c6, 0xa1, 0x77, 0xee, 0x89, 0x48, 0x67, 0xc8, 0xc4);

    /// <summary>MFSampleExtension_CleanPoint {9CDF01D8-A0F0-43BA-B077-EAA06CBD728A}（mfapi.h）：
    /// sample 级 UINT32 属性，非零 = 关键帧（clean point / sync sample）。</summary>
    internal static readonly Guid MFSampleExtension_CleanPoint = new(0x9cdf01d8, 0xa0f0, 0x43ba, 0xb0, 0x77, 0xea, 0xa0, 0x6c, 0xbd, 0x72, 0x8a);

    /// <summary>MF_MT_MPEG4_SAMPLE_DESCRIPTION {261E9D83-9529-4B8F-A111-8B9C950A81A9}（mfapi.h）。
    /// MF MPEG-4 媒体源把 MP4 stsd 盒整体透传到该 Blob 属性；avcC（SPS/PPS）需从中手工解析——
    /// 本机验证 MF 不会在媒体类型上填 MF_MT_MPEG_SEQUENCE_HEADER（native/current/prime 后均 MF_E_ATTRIBUTENOTFOUND）。</summary>
    internal static readonly Guid MF_MT_MPEG4_SAMPLE_DESCRIPTION = new(0x261e9d83, 0x9529, 0x4b8f, 0xa1, 0x11, 0x8b, 0x9c, 0x95, 0x0a, 0x81, 0xa9);
    internal static readonly Guid MF_MT_FRAME_SIZE = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    // ⚠️ 以下四个键 2026-07-31 比对 Windows SDK 10.0.26100.0 mfapi.h 原文修正——早期臆测值全部错误，
    //    导致 GetUINT32 恒返回 MF_E_ATTRIBUTENOTFOUND、音频轨采样率/声道/位深恒为 0（WASAPI 被以 0Hz/0ch 初始化）。
    internal static readonly Guid MF_MT_FRAME_RATE = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    internal static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new(0x5faeeae7, 0x0290, 0x4c31, 0x9e, 0x8a, 0xc5, 0x34, 0xf6, 0x8d, 0x9d, 0xba);
    internal static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new(0x37e48bf5, 0x645e, 0x4c5b, 0x89, 0xde, 0xad, 0xa9, 0xe2, 0x9b, 0x69, 0x6a);
    internal static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new(0xf2deb57f, 0x40fa, 0x4764, 0xaa, 0x33, 0xed, 0x4f, 0x2d, 0x1f, 0xf6, 0x69);

    // Major type GUIDs
    internal static readonly Guid MFMediaType_Video = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid MFMediaType_Audio = new(0x73647561, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // Video subtype GUIDs
    internal static readonly Guid MFVideoFormat_H264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "H264"
    internal static readonly Guid MFVideoFormat_H265 = new(0x35363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "H265"
    internal static readonly Guid MFVideoFormat_HEVC = new(0x43564548, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "HEVC"（系统 H265 解码 MFT 注册的输入 subtype）
    internal static readonly Guid MFVideoFormat_NV12 = new(0x3231564e, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "NV12"
    internal static readonly Guid MFVideoFormat_RGB32 = new(0x00000016, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // Audio subtype GUIDs
    internal static readonly Guid MFAudioFormat_PCM = new(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid MFAudioFormat_AAC = new(0x00001610, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid MFAudioFormat_MP3 = new(0x00000055, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // HRESULT
    internal const int S_OK = 0;
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9); // 原值 0xC01D4005 有误（SDK mferror.h 真值）；使用点均有 hr<0 兜底，无行为变化
    internal const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    internal const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61); // 原值 0xC00D6D71 有误；本机 H264 MFT 运行时实抛 0xC00D6D61（mferror.h 真值）
    internal const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5); // ProcessInput：MFT 当前不接受更多输入（须先取输出）

    // MFT 枚举分类 GUID（MFTEnum 动态发现注册的解码 MFT，避免硬编码 CLSID 在未注册 / HEVC 可选的系统上失败）
    // mfapi.h 权威值 {d6c02d4b-6833-45b4-971a-05a4b04bab91}；本机运行时验证（2026-07-29）：此 GUID 枚举 H264 → count=1（CLSID_MSH264DecoderMFT）。
    // ⚠️ 早期版本误写 9EA2FB4D-...（臆测值，枚举恒 count=0 → "无注册 MFT"假象），勿回退。
    internal static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new(0xd6c02d4b, 0x6833, 0x45b4, 0x97, 0x1a, 0x05, 0xa4, 0xb0, 0x4b, 0xab, 0x91);

    // MFTEnum（旧 API）Flags 参数按 MSDN 为 Reserved 必须传 0；下列 MFT_ENUM_FLAG_* 仅供未来 MFTEnumEx 使用
    internal const uint MFT_ENUM_FLAG_SYNCHRONOUS = 0x00000080; // 仅同步 MFT（MFTEnumEx 专用）
    internal const uint MFT_ENUM_FLAG_HARDWARE = 0x00000008;    // 硬件 MFT（可能需 D3D 设备管理器）
    internal const uint MFT_ENUM_FLAG_ALL = 0x00000000;         // 枚举全部（同步 + 异步 + 硬件）

    // IMFTransform IID（mftransform.h 权威值 {bf94c121-5b05-4e6f-8000-ba598961414d}；
    // 本机运行时验证（2026-07-29）：CoCreateInstance + 全 vtable 槽位 S_OK。⚠️ 早期误写 ...8009-456E31185733（臆测值），勿回退）
    internal static readonly Guid IID_IMFTransform = new(0xbf94c121, 0x5b05, 0x4e6f, 0x80, 0x00, 0xba, 0x59, 0x89, 0x61, 0x41, 0x4d);

    // MFT 设置类型标志
    internal const int MFT_SET_TYPE_TEST_ONLY = 0x00000001;

    // MF_MT_FRAME_SIZE 编码：(width << 32) | height（UINT64）
    internal static ulong MakeFrameSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;
}
