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
    internal static readonly Guid MFVideoFormat_HEVC = new(0x43564548, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "HEVC"（系统 H265 解码 MFT 注册的输入 subtype，hvc1/hev1 容器常用）
    internal static readonly Guid MFVideoFormat_HEVC_ES = new(0x53564548, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71); // "HEVS"（HEVC 裸流，FCC 与 mfapi.h:2314 对齐）
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

    // MFTEnumEx 枚举标志（mfapi.h:2018-2029 权威值；旧 MFTEnum 的 Flags 仍保留 0）。
    // 尤其 HEVC 视频扩展（Store 安装）注册为异步 MFT，MFTEnum 旧 API 枚举不到，必须用 MFTEnumEx
    // 并包含 ASYNCMFT + HARDWARE + UNTRUSTED_STOREMFT 才能发现。
    internal const uint MFT_ENUM_FLAG_SYNCMFT              = 0x00000001;
    internal const uint MFT_ENUM_FLAG_ASYNCMFT             = 0x00000002;
    internal const uint MFT_ENUM_FLAG_HARDWARE               = 0x00000004;
    internal const uint MFT_ENUM_FLAG_FIELDOFUSE           = 0x00000008;
    internal const uint MFT_ENUM_FLAG_LOCALMFT             = 0x00000010;
    internal const uint MFT_ENUM_FLAG_TRANSCODE_ONLY       = 0x00000020;
    internal const uint MFT_ENUM_FLAG_SORTANDFILTER        = 0x00000040;
    internal const uint MFT_ENUM_FLAG_ALL                  = 0x0000003F; // 同步+异步+硬件（含过滤）
    internal const uint MFT_ENUM_FLAG_UNTRUSTED_STOREMFT   = 0x00000400; // Store 下载的 MFT（HEVC 扩展）

    // MFTEnumEx 返回的 IMFActivate 上取 CLSID 的属性键（mftransform.h:1644 权威值）
    internal static readonly Guid MFT_TRANSFORM_CLSID_Attribute = new(0x6821c42b, 0x65a4, 0x4e82, 0x99, 0xbc, 0x9a, 0x88, 0x20, 0x5e, 0xcd, 0xc);

    // MFT 身份取证（用于判定 SourceReader 内部到底选了「厂商硬件 MFT」还是「微软软件 MFT」）：
    // MFT_FRIENDLY_NAME_Attribute（mftransform.h 权威值 {314FFBAE-5B41-4C95-9C19-4E7D586FACE3}）—— 人类可读名，WCHAR*
    internal static readonly Guid MFT_FRIENDLY_NAME_Attribute = new(0x314ffbae, 0x5b41, 0x4c95, 0x9c, 0x19, 0x4e, 0x7d, 0x58, 0x6f, 0xac, 0xe3);
    // MFT_ENUM_HARDWARE_URL_Attribute（mftransform.h 权威值 {2FB866AC-B078-4942-AB6C-003D05CDA674}）
    // 🔴 官方判据：该属性【存在即为硬件 MFT（MFT_ENUM_FLAG_HARDWARE）】，不存在即软件 MFT。
    //    这是区分「AMD/NV 厂商硬件 MFT」与「微软内置软件 MFT（虽 D3D11_AWARE=1 但走 DXVA 后读回）」的唯一可靠依据。
    internal static readonly Guid MFT_ENUM_HARDWARE_URL_Attribute = new(0x2fb866ac, 0xb078, 0x4942, 0xab, 0x6c, 0x00, 0x3d, 0x05, 0xcd, 0xa6, 0x74);

    // IMFTransform IID（mftransform.h 权威值 {bf94c121-5b05-4e6f-8000-ba598961414d}；
    // 本机运行时验证（2026-07-29）：CoCreateInstance + 全 vtable 槽位 S_OK。⚠️ 早期误写 ...8009-456E31185733（臆测值），勿回退）
    internal static readonly Guid IID_IMFTransform = new(0xbf94c121, 0x5b05, 0x4e6f, 0x80, 0x00, 0xba, 0x59, 0x89, 0x61, 0x41, 0x4d);

    // ── DXVA 零拷贝（V2-15 扩展）所需 IID 与消息常量 ──
    // IMFDXGIDeviceManager（dxva2api.h 权威值 {AEC1CAF6-EE55-44FC-BC6A-E049C3F6D664}）
    internal static readonly Guid IID_IMFDXGIDeviceManager = new(0xaec1caf6, 0xee55, 0x44fc, 0xbc, 0x6a, 0xe0, 0x49, 0xc3, 0xf6, 0xd6, 0x64);
    // IMFDXGIBuffer（dxva2api.h 权威值 {D8AD0F58-EE55-4E44-AB48-5F6EA27FFB15}）
    internal static readonly Guid IID_IMFDXGIBuffer = new(0xd8ad0f58, 0xee55, 0x4e44, 0xab, 0x48, 0x5f, 0x6e, 0xa2, 0x7f, 0xfb, 0x15);
    // ID3D11Texture2D（d3d11.h 权威值 {6F15AAF2-D208-4E89-9AB4-489535D34F9C}）
    internal static readonly Guid IID_ID3D11Texture2D = new(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);
    // ID3D10Multithread（d3d10.h:7119 DEFINE_GUID 权威值 {9B7E4E00-342C-4106-A19F-4F2704F689F0}）
    // DXVA 共享设备必须开多线程保护：解码 MFT 与渲染在不同线程访问同一 ID3D11Device，
    // 未开保护时 D3D11 运行时不做内部同步 ⇒ 竞态/设备移除（MSDN "Direct3D 11 Video APIs" 硬性要求）。
    internal static readonly Guid IID_ID3D10Multithread = new(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);
    // MF_SA_D3D11_AWARE（mftransform.h:1618 权威值 {206B4FC8-FCF9-4C51-AFE3-9764369E33A0}）
    // MFT 属性：UINT32 非 0 表示该 MFT 支持 Direct3D 11 视频解码。发 SET_D3D_MANAGER 前必须探测——
    // 不 aware 的 MFT 对未知/不支持消息普遍返回 S_OK 忽略，会制造「假激活」。
    internal static readonly Guid MF_SA_D3D11_AWARE = new(0x206b4fc8, 0xfcf9, 0x4c51, 0xaf, 0xe3, 0x97, 0x64, 0x36, 0x9e, 0x33, 0xa0);
    // IMFMediaBuffer（mfobjects.h 权威值 {045FA593-8799-42B8-BC8D-8968C6453508}）
    internal static readonly Guid IID_IMFMediaBuffer = new(0x045fa593, 0x8799, 0x42b8, 0xbc, 0x8d, 0x89, 0x68, 0xc6, 0x45, 0x35, 0x07);
    // IMF2DBuffer（mfobjects.h 权威值 {7DC9D5F9-9ED9-44EC-9BBF-0600BB589F56}）
    internal static readonly Guid IID_IMF2DBuffer = new(0x7dc9d5f9, 0x9ed9, 0x44ec, 0x9b, 0xbf, 0x06, 0x00, 0xbb, 0x58, 0x9f, 0x56);

    // ── SourceReader 内部 MFT 链取证（2026-08-07，零拷贝失效根因定位）────────────────────────
    // IMFSourceReaderEx（mfreadwrite.h:644 MIDL_INTERFACE 权威值 {7B981CF0-560E-4116-9875-B099895F23D7}）
    // 🔴 唯一能看穿 SourceReader「黑盒」的接口：GetTransformForStream 可枚举它为某条流实际插入的 MFT 链。
    //    宪法「S_OK≠被接受：能力自报+行为副作用双判据」在此落地——我们设了 D3D_MANAGER 且全部返回 S_OK，
    //    但样本仍非 DXGI，必须直接查证 SourceReader 到底建了什么拓扑（是否偷插 Video Processor 把帧拉回内存）。
    internal static readonly Guid IID_IMFSourceReaderEx = new(0x7b981cf0, 0x560e, 0x4116, 0x98, 0x75, 0xb0, 0x99, 0x89, 0x5f, 0x23, 0xd7);
    // MFT_CATEGORY_VIDEO_DECODER 已在上方（第 104 行附近，MFTEnum 用）定义，此处直接复用，勿重复声明。
    // MFT_CATEGORY_VIDEO_PROCESSOR（mfapi.h:1907 权威值 {302EA3FC-AA5F-47F9-9F7A-C2188BB16302}）
    // 🔴 出现在链上即为零拷贝头号杀手：VP 会把 DXGI 表面拉回系统内存做格式/尺寸转换。
    internal static readonly Guid MFT_CATEGORY_VIDEO_PROCESSOR = new(0x302ea3fc, 0xaa5f, 0x47f9, 0x9f, 0x7a, 0xc2, 0x18, 0x8b, 0xb1, 0x63, 0x02);
    // MFT_CATEGORY_VIDEO_EFFECT（mfapi.h:1882 权威值 {12E17C21-532C-4A6E-8A1C-40825A736397}）
    internal static readonly Guid MFT_CATEGORY_VIDEO_EFFECT = new(0x12e17c21, 0x532c, 0x4a6e, 0x8a, 0x1c, 0x40, 0x82, 0x5a, 0x73, 0x63, 0x97);
    // IMF2DBuffer2（mfobjects.h:1642 MIDL_INTERFACE 权威值 {33AE5EA6-4316-436F-8DDD-D73D22F829EC}）
    // 🔴 2026-08-07 A 方案半 DXVA 治本：MS H264 MFT 半 DXVA 时把帧读回 Direct3DSurface9-backed 2D 内存，
    //    实际 pitch 是 16 字节对齐（如 1080→1088），用 ConvertToContiguousBuffer 拿不到真值→按紧凑 stride 拷贝→画面横纹错位。
    //    必须 QI 此 IID 后 Lock2D 取真值 stride 与 scanline0，逐行拷贝到紧凑布局，下游渲染器零分支。
    internal static readonly Guid IID_IMF2DBuffer2 = new(0x33ae5ea6, 0x4316, 0x436f, 0x8d, 0xdd, 0xd7, 0x3d, 0x22, 0xf8, 0x29, 0xec);
    // MF_MT_VIDEO_NOMINAL_RANGE（mfapi.h 权威值 {C21B8EE5-B956-4071-917B-3894AA8DB48B}）
    internal static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new(0xc21b8ee5, 0xb956, 0x4071, 0x91, 0x7b, 0x38, 0x94, 0xaa, 0x8d, 0xb4, 0x8b);
    // MF_MT_DEFAULT_STRIDE {644B4E48-1E02-4516-B0EB-C01CA9D49AC6}（UINT32 存 INT32，可为负=bottom-up；单位字节）
    // 🔴 2026-08-07 逐字节核对修正：原值写作 {644B4424-1063-42B4-B200-E6970237AD6B}（注释还标着"mfapi.h 权威值"），
    //    但该 GUID 在整个 Windows SDK 头文件里【根本不存在】——属臆造。以本机实物为准：
    //    E:\Windows Kits\10\Include\10.0.26100.0\um\mfapi.h:3276 DEFINE_GUID(MF_MT_DEFAULT_STRIDE,
    //    0x644b4e48, 0x1e02, 0x4516, 0xb0, 0xeb, 0xc0, 0x1c, 0xa9, 0xd4, 0x9a, 0xc6);
    //    旧值只被 DXVA 深度诊断字符串引用 ⇒ 表现为 "DefaultStride=缺失" 恒真（静默失效，非崩溃），
    //    但 A 方案的 NV12 CPU 回落要靠它算行跨度，写错会直接花屏，故必须修。
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new(0x644b4e48, 0x1e02, 0x4516, 0xb0, 0xeb, 0xc0, 0x1c, 0xa9, 0xd4, 0x9a, 0xc6);

    // ── H264 DXVA 设备能力真值探测（决定性判据：区分「半 DXVA 读回」与「真 DXGI 零拷贝」）──
    // ID3D11VideoDevice（d3d11.h:13727 MIDL_INTERFACE 权威值 {10EC4D5B-975A-4689-B9E4-D0AAC30FE333}）
    // 🔴 旧记忆里的 1F010207-... 是 ID3D11VideoContext，非 VideoDevice——务必以 SDK 实物为准。
    internal static readonly Guid IID_ID3D11VideoDevice = new(0x10ec4d5b, 0x975a, 0x4689, 0xb9, 0xe4, 0xd0, 0xaa, 0xc3, 0x0f, 0xe3, 0x33);
    // D3D11_DECODER_PROFILE_H264_VLD_NOFGT（d3d11.h:10039 DEFINE_GUID 权威值 {1B81BE68-A0C7-11D3-B984-00C04F2E73C5}）
    // 标准 H264 主/高规 VLD 解码 profile（无 film grain）。勿用臆测的 42EE2D3C-...（那是 DXVA2 其它 profile）。
    internal static readonly Guid D3D11_DECODER_PROFILE_H264_VLD_NOFGT = new(0x1b81be68, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5);
    // DXGI_FORMAT_NV12（dxgiformat.h:116 权威值 = 103；⚠️ 非 167，手敲易错）
    internal const int DXGI_FORMAT_NV12 = 103;
    // ID3D11Device（d3d11.h:1395 MIDL_INTERFACE 权威值 {1841E5C8-16B0-489B-BCC8-44CFB0D5DEAE}）
    internal static readonly Guid IID_ID3D11Device = new(0x1841e5c8, 0x16b0, 0x489b, 0xbc, 0xc8, 0x44, 0xcf, 0xb0, 0xd5, 0xde, 0xae);
    // D3D11_DECODER_PROFILE_H264_VLD_FGT（d3d11.h:10040 DEFINE_GUID 权威值 {1B81BE69-...}）标准 H264 VLD 带 film grain。
    internal static readonly Guid D3D11_DECODER_PROFILE_H264_VLD_FGT = new(0x1b81be69, 0xa0c7, 0x11d3, 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5);
    // D3D11_DECODER_PROFILE_HEVC_VLD_MAIN（d3d11.h:10058 权威值 {5B11D51B-2F4C-4452-BCC3-09F2A1160CC0}）标准 HEVC 主规 VLD 解码 profile。
    internal static readonly Guid D3D11_DECODER_PROFILE_HEVC_VLD_MAIN = new(0x5b11d51b, 0x2f4c, 0x4452, 0xbc, 0xc3, 0x09, 0xf2, 0xa1, 0x16, 0x0c, 0xc0);
    // D3D11_DECODER_PROFILE_HEVC_VLD_MAIN10（d3d11.h:10059 权威值 {107AF0E0-EF1A-4D19-ABA8-67A163073D13}）HEVC Main10（10bit）VLD 解码 profile。
    internal static readonly Guid D3D11_DECODER_PROFILE_HEVC_VLD_MAIN10 = new(0x107af0e0, 0xef1a, 0x4d19, 0xab, 0xa8, 0x67, 0xa1, 0x63, 0x07, 0x3d, 0x13);


    // 🔴 MFT_MESSAGE_SET_D3D_MANAGER = 0x2（mftransform.h:174 SDK 实物）。
    //    **不存在** MFT_MESSAGE_SET_D3D11_MANAGER 这个枚举项 —— MFT_MESSAGE_TYPE 全集只有
    //    FLUSH=0 / DRAIN=1 / SET_D3D_MANAGER=2 / DROP_SAMPLES=3 / COMMAND_TICK=4 /
    //    NOTIFY_*=0x10000000..0x10000008 / COMMAND_MARKER=0x20000000。
    //    D3D9(DXVA2) 与 D3D11(DXGI) **共用本消息**，区别仅在 ulParam 传 IDirect3DDeviceManager9*
    //    还是 IMFDXGIDeviceManager*（MSDN "Supporting Direct3D 11 Video Decoding in Media Foundation"）。
    //    2026-08-04 曾臆造 0x80000013：MFT 对未知消息按约定返回 S_OK 静默忽略 ⇒ 日志「硬解已激活」
    //    却全程软解（GPU零拷贝=0、每帧 QI(IMFDXGIBuffer) 得 E_NOINTERFACE）。此为「假绿」真根因。
    internal const int MFT_MESSAGE_SET_D3D_MANAGER = 0x00000002;

    // MFT_TRANSFORM_FLAGS_Attribute（mfapi.h:1640 权威值 {9359BB7E-6275-46C4-A025-1C01E45F1A86}）：UINT32 属性，
    // 低 bit0 = MFT_TRANSFORM_ASYNC（0x1）→ 异步 MFT（Store 安装的 HEVC 扩展等）。同步 MFT 该位为 0。
    internal static readonly Guid MF_TRANSFORM_FLAGS_Attribute = new(0x9359bb7e, 0x6275, 0x46c4, 0xa0, 0x25, 0x1c, 0x01, 0xe4, 0x5f, 0x1a, 0x86);

    // MFT 设置类型标志
    internal const int MFT_SET_TYPE_TEST_ONLY = 0x00000001;

    // ══════════════════════════════════════════════════════════════════════════════════
    // ── SourceReader 自带硬解 + DXGI 出样（A 方案：官方验证的 MF 零拷贝正路）所需属性键 ──
    //
    // 背景：直连 MFT（MFTEnumEx → ActivateObject → SET_D3D_MANAGER → ProcessInput/Output）在部分
    // GPU/驱动组合上会在内部 CreateVideoDecoder 阶段静默回落软件解码 —— MFT 仍报 PROVIDES_SAMPLES=True，
    // 但每帧 buffer QI(IMFDXGIBuffer) 得 E_NOINTERFACE（本机 mp4/H264 实测「第二层半 DXVA」）。
    // 官方推荐路径是让 IMFSourceReader 自己承载解码 MFT：在 MFCreateSourceReaderFromURL 的 attributes 上
    // 挂 MF_SOURCE_READER_D3D_MANAGER + MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS，SourceReader 会替我们
    // 完成「选 MFT → 发 SET_D3D_MANAGER → 分配 DXGI 表面池」的全套编排，ReadSample 直接吐 DXGI 纹理样本。
    //
    // 🔴 全部 GUID 已逐字节比对本机 SDK 实物：E:\Windows Kits\10\Include\10.0.26100.0\um\mfreadwrite.h
    //    （行号见各条注释）。手写 GUID 一律以 SDK 头文件为准，勿凭记忆/臆测。
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>MF_SOURCE_READER_D3D_MANAGER {EC822DA2-E1E9-4B29-A0D8-563C719F5269}（mfreadwrite.h:290）。
    /// IUnknown 属性：把 <c>IMFDXGIDeviceManager*</c> 交给 SourceReader，令其内部解码 MFT 在该 D3D11 设备上
    /// 分配输出表面 ⇒ ReadSample 返回的 IMFSample 其 buffer 可 QI 出 IMFDXGIBuffer（真 GPU 纹理，零拷贝）。
    /// 必须用 <c>IMFAttributes::SetUnknown</c>（slotIndex 24）写入，不能用 SetUINT64 传裸指针。</summary>
    internal static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new(0xec822da2, 0xe1e9, 0x4b29, 0xa0, 0xd8, 0x56, 0x3c, 0x71, 0x9f, 0x52, 0x69);

    /// <summary>MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS {A634A91C-822B-41B9-A494-4DE4643612B0}（mfreadwrite.h:2071）。
    /// UINT32：非 0 允许 SourceReader/SinkWriter 使用硬件 MFT（GPU 厂商解码器）。
    /// 不设时只用软件 MFT ⇒ 即便挂了 D3D manager 也拿不到 DXGI 表面。</summary>
    internal static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new(0xa634a91c, 0x822b, 0x41b9, 0xa4, 0x94, 0x4d, 0xe4, 0x64, 0x36, 0x12, 0xb0);

    /// <summary>MF_SOURCE_READER_DISABLE_DXVA {AA456CFD-3943-4A1E-A77D-1838C0EA2E35}（mfreadwrite.h:291）。
    /// UINT32：1 = 禁用 DXVA（强制软解）。零拷贝路径显式写 0 表达意图（默认亦为 0），
    /// 软解兜底重开 SourceReader 时写 1 彻底关掉硬件路径，避免驱动半吊子回落。</summary>
    internal static readonly Guid MF_SOURCE_READER_DISABLE_DXVA = new(0xaa456cfd, 0x3943, 0x4a1e, 0xa7, 0x7d, 0x18, 0x38, 0xc0, 0xea, 0x2e, 0x35);

    /// <summary>MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING {0F81DA2C-B537-4672-A8B2-A681B17307A3}（mfreadwrite.h:296）。
    /// UINT32：允许 SourceReader 插入 Video Processor MFT 做格式/尺寸转换。
    /// ⚠️ 零拷贝路径**不要**开启：VPU 转换会把样本落到系统内存，破坏 DXGI 直通（仅软解兜底路径才考虑）。</summary>
    internal static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new(0x0f81da2c, 0xb537, 0x4672, 0xa8, 0xb2, 0xa6, 0x81, 0xb1, 0x73, 0x07, 0xa3);

    /// <summary>MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING {FB394F3D-CCF1-42EE-BBB3-F9B845D5681D}（mfreadwrite.h:294）。
    /// UINT32：基础色彩转换开关。同样会破坏零拷贝，仅软解兜底路径可用。</summary>
    internal static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new(0xfb394f3d, 0xccf1, 0x42ee, 0xbb, 0xb3, 0xf9, 0xb8, 0x45, 0xd5, 0x68, 0x1d);

    // MF_MT_FRAME_SIZE 编码：(width << 32) | height（UINT64）
    internal static ulong MakeFrameSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;
}
