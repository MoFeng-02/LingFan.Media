using Microsoft.Extensions.Logging;

namespace LingFan.Media.AvaloniaTools.Android;

/// <summary>
/// 直写 Android.Util.Log 的 ILoggerProvider——唯一的可靠 logcat 通道：
/// Console（stdout）与 Debug provider 均依赖 VS 调试通道，Fast Deployment / 无调试器下不进 logcat。
/// tag 固定 "DOTNET"，与 .NET Android 运行时 tag 一致，便于统一过滤。
/// 注意：本命名空间以 Android 结尾，与平台命名空间遮蔽，Android.Util 一律 global:: 全限定；
/// Android.Util.Log 又与 ILogger.Log 方法同名——双重遮蔽，只能 global::。
/// </summary>
public sealed class LogCatLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LogCatLogger(categoryName);

    public void Dispose() { }

    private sealed class LogCatLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            global::Android.Util.LogPriority priority = logLevel switch
            {
                LogLevel.Critical => global::Android.Util.LogPriority.Assert,
                LogLevel.Error => global::Android.Util.LogPriority.Error,
                LogLevel.Warning => global::Android.Util.LogPriority.Warn,
                LogLevel.Information => global::Android.Util.LogPriority.Info,
                _ => global::Android.Util.LogPriority.Debug,
            };
            string message = $"{category}: {formatter(state, exception)}";
            global::Android.Util.Log.WriteLine(priority, "DOTNET", message);
            if (exception is not null)
                global::Android.Util.Log.WriteLine(priority, "DOTNET", exception.ToString());
        }
    }
}
