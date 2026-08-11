using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// 原生互操作操作追踪器 —— 即「错误链 / 完整错误调度链」。
/// </summary>
/// <remarks>
/// <para>动机：MF 冷启动触发 <c>COR_E_EXECUTIONENGINE</c>（原生堆损坏），
/// 但堆损坏不在破坏点崩溃，而在「下一次 CLR 内部堆操作」才以滞后症状暴露，崩溃栈永远指向
/// <c>Marshal.GetDelegateForFunctionPointer</c> 这种无辜调用 —— 看不到真正的错配 pair。</para>
/// <para>本类把每一条原生配对操作（Lock/Unlock、GetBuffer/ReleaseBuffer、Map/Unmap、Marshal.Release、MfVTable.Get）
/// 记录进一个<b>无分配环形缓冲</b>（时间戳 + 线程 + 指针 + 调用点 + HRESULT），使 post-mortem 时
/// 最后若干条记录能直接指认「哪个指针被错序释放 / 在哪个调用点被 use-after-free」。</para>
/// <para><b>实时护栏</b>（仅 <c>LINGFAN_INTEROP_STRICT=1</c> 启用，避免热路径加锁/分配改变堆时序）：
/// 检测三类确定性违规并抛出<b>可诊断</b>的 <see cref="InteropViolationException"/>（精确指名道姓），
/// 把静默的堆损坏变成「当场在违规点崩溃」——这正是把滞后症状变成精确凶手的关键：</para>
/// <list type="bullet">
/// <item>① <b>重复 Release（double-free）</b>：对同一地址执行第二次 <c>Marshal.Release</c>。
/// 由 <see cref="OnAlloc"/> 在重新分配时清除该地址的陈旧「已释放」标记，杜绝 LFH 地址复用导致的误报，故判定严谨。</item>
/// <item>② <b>未 Lock 即 Unlock / 重复 Unlock</b>（COM 配对同构违规）：按每缓冲 Lock 深度校验，确定性可判。</item>
/// </list>
/// <para><b>关于 use-after-free（UAF）</b>：朴素「已释放地址集合」判定会因 Windows LFH 堆地址复用
/// 产生<b>误报</b>（新分配恰好复用已释放地址），且对「悬垂引用指向被复用地址」反而<b>漏报</b>，故 tracer 不在
/// vtable 取用时硬抛 UAF。严谨的 UAF 定位交由 <b>Windows Application Verifier / PageHeap（GFlags）</b>在 OS 层完成——
/// 它对原生堆设全页校验，任何 UAF/double-free 都在<b>违规那一条指令</b>当场崩，零误报且报出精确地址。
/// tracer 此处仅把每条 vtable 取用记入环形缓冲供 post-mortem 比对。</para>
/// <para>AOT 兼容：纯 BCL，无反射、无动态代码生成；环形缓冲预分配，热路径零分配。</para>
/// </remarks>
internal static class InteropTrace
{
    /// <summary>原生操作种类（用于环形记录与护栏分类）。</summary>
    internal enum Op : byte
    {
        Acquire, Release, ReleaseDouble,
        Lock, LockFail, Unlock, UnlockNoLock,
        GetBuffer, ReleaseBuffer, Map, Unmap, UnmapNoMap,
        VTableGet, VTableGetOnReleased,
        Custom
    }

    [DebuggerDisplay("{Kind} ptr=0x{Ptr} site={Site} hr=0x{Hr:X8}")]
    private struct Entry
    {
        public long Ts;
        public int Tid;
        public Op Kind;
        public IntPtr Ptr;
        public string Site;
        public int Hr;
    }

    private const int CAP = 16384;
    private static readonly Entry[] _ring = new Entry[CAP];
    private static int _pos;

    private static readonly bool _strict =
        string.Equals(
            System.Environment.GetEnvironmentVariable("LINGFAN_INTEROP_STRICT"),
            "1", System.StringComparison.Ordinal);

    // —— 实时护栏状态（仅严格模式维护）——
    private static readonly object _guardLock = new();
    private static readonly HashSet<IntPtr> _released = new();
    private static readonly Dictionary<IntPtr, int> _lockDepth = new();
    private const int MAX_TRACKED = 16384;

    private static long NowTs => Stopwatch.GetTimestamp();

    private static void Append(Op kind, IntPtr ptr, string site, int hr = 0)
    {
        int idx = (int)((uint)System.Threading.Interlocked.Increment(ref _pos) % CAP);
        ref Entry e = ref _ring[idx];
        e.Ts = NowTs;
        e.Tid = Environment.CurrentManagedThreadId;
        e.Kind = kind;
        e.Ptr = ptr;
        e.Site = site;
        e.Hr = hr;
    }

    // ───────────────────────── 公开接口 ─────────────────────────

    /// <summary>在 <see cref="MfVTable.Get"/> 入口调用：仅记录（post-mortem 用）。
    /// 不在此时做「已释放地址集合」UAF 判定——LFH 堆地址复用会使该判定误报，且对真 UAF 反而漏报；
    /// 严谨的 UAF 定位交由 PageHeap(GFlags) 在 OS 层完成（见类文档）。</summary>
    public static void OnVTableGet(IntPtr comPtr, string site)
    {
        Append(Op.VTableGet, comPtr, site);
    }

    /// <summary>在原生对象【分配/创建】成功后调用：清除该地址上可能残留的「已释放」陈旧标记，
    /// 避免后续对该（被堆复用的）新对象误判为 UAF / 重复释放（详见类文档关于 LFH 地址复用的说明）。</summary>
    public static void OnAlloc(IntPtr ptr, string site)
    {
        Append(Op.Acquire, ptr, site);
        if (_strict && ptr != IntPtr.Zero)
        {
            lock (_guardLock)
            {
                _released.Remove(ptr);
                _lockDepth.Remove(ptr);
            }
        }
    }

    /// <summary>等价 <see cref="Marshal.Release"/>，额外做重复释放检测并维护 UAF 集合。</summary>
    public static int ReleaseComPtr(IntPtr ptr, string site)
    {
        Append(Op.Release, ptr, site);
        if (_strict && ptr != IntPtr.Zero)
        {
            lock (_guardLock)
            {
                if (!_released.Add(ptr))
                    Throw("对同一 COM 指针执行第二次 Marshal.Release（double-free）", site, ptr);
                _lockDepth.Remove(ptr); // 该指针尚处 Lock 态即被释放：极危险，但至少移除避免误判
                if (_released.Count > MAX_TRACKED) _released.Clear();
            }
        }
        return ptr != IntPtr.Zero ? Marshal.Release(ptr) : 0;
    }

    /// <summary>等价 <c>IMFMediaBuffer.Lock</c>，维护每缓冲 Lock 深度用于 Unlock 配对校验。</summary>
    public static int LockBuffer(IntPtr buffer, IMFMediaBuffer_Lock lockDel,
        out IntPtr data, out uint maxLen, out uint curLen, string site)
    {
        Append(Op.Lock, buffer, site);
        int hr = lockDel(buffer, out data, out maxLen, out curLen);
        if (hr < 0)
            Append(Op.LockFail, buffer, site, hr);
        else if (_strict)
        {
            lock (_guardLock)
            {
                _lockDepth.TryGetValue(buffer, out int d);
                _lockDepth[buffer] = d + 1;
            }
        }
        return hr;
    }

    /// <summary>等价 <c>IMFMediaBuffer.Unlock</c>，校验存在对应成功 Lock。</summary>
    public static void UnlockBuffer(IntPtr buffer, IMFMediaBuffer_Unlock unlockDel, string site)
    {
        Append(Op.Unlock, buffer, site);
        if (_strict)
        {
            lock (_guardLock)
            {
                if (!_lockDepth.TryGetValue(buffer, out int d) || d <= 0)
                    Throw("Unlock 无对应成功 Lock（未 Lock 即 Unlock / 重复 Unlock）", site, buffer);
                if (d == 1) _lockDepth.Remove(buffer);
                else _lockDepth[buffer] = d - 1;
            }
        }
        unlockDel(buffer);
    }

    /// <summary>仅记录型 Hook（WASAPI GetBuffer/ReleaseBuffer、Vulkan/D3D11 Map/Unmap 等接口各异处）。</summary>
    public static void Note(Op kind, IntPtr ptr, string site, int hr = 0) => Append(kind, ptr, site, hr);

    // ───────────────────────── 错误抛出 ─────────────────────────

    private static void Throw(string msg, string site, IntPtr ptr) =>
        throw new InteropViolationException(
            $"[InteropTrace] {msg}；调用点={site}；指针=0x{ptr:X}。" +
            $"这是原生堆损坏类的【可诊断前兆】（非滞后症状），据此定位即可。");

    // ───────────────────────── Post-mortem ─────────────────────────

    /// <summary>把环形缓冲最近若干条写入临时文件，供崩溃后 post-mortem 比对（正常退出/关闭时调用）。</summary>
    public static void Dump(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), "lingfan-interop-trace.log");
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# LingFan InteropTrace dump @ {DateTime.UtcNow:O} (strict={_strict})");
            int start = ((_pos - 1) % CAP + CAP) % CAP;
            for (int i = 0; i < CAP; i++)
            {
                int idx = ((start - i) % CAP + CAP) % CAP;
                ref Entry e = ref _ring[idx];
                if (e.Site is null) continue;
                char mark = i == 0 ? '▶' : ' ';
                sb.AppendLine($"{mark} +{Stopwatch.GetElapsedTime(e.Ts).TotalMilliseconds:F1}ms " +
                              $"tid={e.Tid} {e.Kind} ptr=0x{e.Ptr:X} {e.Site} hr=0x{e.Hr:X8}");
            }
            File.AppendAllText(path, sb.ToString());
        }
        catch { /* 尽力而为 */ }
    }
}

/// <summary>原生互操作护栏检测到的确定性违规（可诊断，非滞后症状）。</summary>
internal sealed class InteropViolationException : Exception
{
    public InteropViolationException(string message) : base(message) { }
}
