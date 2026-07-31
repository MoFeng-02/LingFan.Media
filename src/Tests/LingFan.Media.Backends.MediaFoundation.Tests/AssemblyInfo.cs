using Xunit;

// ⚠️ 禁用测试并行（2026-07-31 审计修复，勿删）：
// 本程序集内所有测试类均触达进程级原生全局状态——MFStartup/MFShutdown（进程级引用计数，
// 计数归 0 时整个 MF 平台被拆除）、D3D11 设备/SwapChain、COM 单元、WASAPI 音频端点。
// xunit.v3 默认并行执行测试集合（每个测试类一个集合）：一个测试的 MFBackend.Dispose →
// MFShutdown 把计数打到 0 拆平台时，另一并行测试可能正在 IMFSourceReader.ReadSample 内部
// → 原生堆损坏 → 0x80131506（COR_E_EXECUTIONENGINE）非确定性 FailFast。
// 实测复现：同一命令连跑 3 次，Run1 全绿、Run2/Run3 崩溃且崩溃点漂移——典型并发腐蚀特征。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
