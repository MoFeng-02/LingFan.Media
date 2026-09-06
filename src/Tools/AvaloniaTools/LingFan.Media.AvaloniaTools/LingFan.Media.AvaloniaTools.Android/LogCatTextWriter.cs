using System.Text;

namespace LingFan.Media.AvaloniaTools.Android;

/// <summary>
/// 把 <see cref="Console.Out"/> / <see cref="Console.Error"/> 接到 Android.Util.Log 的 TextWriter。
/// </summary>
/// <remarks>
/// Release（无调试器）下 Mono 运行时不再把 stdout 重定向到 logcat，所有 Console.WriteLine
/// 诊断锚点（[ANDROID-VULKAN] / [VULKAN-BIND] 等）随之静默；本类在应用启动期把两个标准流
/// 接回 logcat（tag=DOTNET），恢复 Release 构建的观测能力。按行缓冲，跨线程调用安全。
/// </remarks>
internal sealed class LogCatTextWriter : TextWriter
{
    private readonly global::Android.Util.LogPriority _priority;
    private readonly object _lock = new();
    private string _pending = string.Empty;

    public LogCatTextWriter(global::Android.Util.LogPriority priority) => _priority = priority;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => WriteCore(value.ToString());

    public override void Write(string? value)
    {
        if (value is not null)
            WriteCore(value);
    }

    public override void WriteLine(string? value) => WriteCore((value ?? string.Empty) + "\n");

    private void WriteCore(string value)
    {
        lock (_lock)
        {
            _pending += value;
            int start = 0;
            int nl;
            while ((nl = _pending.IndexOf('\n', start)) >= 0)
            {
                var line = _pending[start..nl].TrimEnd('\r');
                if (line.Length > 0)
                    global::Android.Util.Log.WriteLine(_priority, "DOTNET", line);
                start = nl + 1;
            }
            _pending = _pending[start..];
        }
    }
}
