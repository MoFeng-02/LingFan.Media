using Xunit;

// ⚠️ 禁用测试并行（防御纵深，保留勿删）：
// 本程序集内所有测试类均触达进程级原生全局状态——MF 平台、D3D11 设备/SwapChain、COM 单元、WASAPI 音频端点。
// 历史：曾出现 MF 冷启动 flaky 崩溃（0x80131506），根因为 MFStartup/MFShutdown 进程级 API 被 MFBackend（解封装）
// 与 MFVideoDecoder（解码）两个互不协调的调用者裸调、零引用计数——MediaPlayer 释放顺序「先 Dispose 解码器(→MFShutdown)
// 后 Close 解封装器」会在解封装器读取线程仍 in-flight 原生 ReadSample 时拆掉平台 → 访问违规。
// 该根因已由 MFPlatform（进程级引用计数封装）修复：MF 仅在所有消费者释放后才真正 MFShutdown。
// 此处 DisableTestParallelization 作为纵深防御保留（避免并行测试间共享原生全局状态带来额外噪声），
// 但真正的修复是 MFPlatform 引用计数，而非此属性——勿误以为靠它即可。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
