# 事件与异常

## 事件参数类型

全部五个都位于 `LingFan.Media.Abstractions`。

| 类型 | 携带 | 使用者 |
|------|---------|---------|
| `MediaStateChangedEventArgs` | 旧 / 新 `MediaState` | `IMediaPlayer.StateChanged` |
| `MediaErrorEventArgs` | `MediaErrorCode` + 消息 | `IMediaPlayer.ErrorOccurred` |
| `BufferProgressEventArgs` | 已缓冲时长 / 字节 / 状态 | `IBufferManager.BufferProgressChanged` |
| `TrackChangedEventArgs` | 旧 / 新 `MediaTrack?` | 轨道选择变更 |
| `LogEventArgs` | 级别 + 消息 | 日志记录 |

## 异常

| 类型 | 含义 |
|------|---------|
| `MediaBackendUnsupportedException` | 当**每一个**已注册后端都未能打开源时，由回退中间件抛出。 |
| `GpuDeviceLostException` | 渲染器 / 输出在 `DXGI_DEVICE_REMOVED` / `VK_ERROR_DEVICE_LOST` 时抛出。会话应通过 `OpenAsync` + `Attach` 重建；会话内恢复在 V3 中透明。 |
