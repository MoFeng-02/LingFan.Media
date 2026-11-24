using System.Runtime.InteropServices;

namespace ProbeShared;

/// <summary>
/// 有头探针 user32/gdi32 P/Invoke（[LibraryImport] 源生成，AOT 合规）。
/// </summary>
/// <remarks>
/// [LibraryImport] 源生成要求载体类型为顶级 partial 类型（嵌套类型被 SYSLIB1050 拒绝）。
/// 全部使用 EntryPoint="XxxW" + Utf16 封送；A/W 双版本 API 必须显式 EntryPoint
/// （LibraryImport 默认 ExactSpelling=true，不做后缀探测）。
/// </remarks>
public static partial class NativeMethods
{
    /// <summary>创建窗口（Unicode）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    /// <summary>销毁窗口（须在创建线程上调用）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    /// <summary>非阻塞取消息（PM_REMOVE=1）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    /// <summary>翻译虚拟键消息为字符消息。</summary>
    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    /// <summary>分发消息到窗口过程。</summary>
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    /// <summary>显示/隐藏窗口（SW_SHOW=1）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>取窗口客户区矩形（物理像素，前提是进程 DPI-aware；否则返回被虚拟化的逻辑像素）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>Win10 1607+：取窗口所在监视器的有效 DPI（96 = 100%，120 = 125%，144 = 150%）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>注册窗口类（WNDCLASSEXW），返回类 atom（0=失败，GetLastPInvokeError 取原因）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    /// <summary>默认窗口过程：探针不自处理消息，全部转发 DefWindowProc。</summary>
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取系统标准 GDI 对象（如 BLACK_BRUSH=4）作窗口类背景刷。</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    public static partial IntPtr GetStockObject(int i);

    /// <summary>取系统标准光标（IDC_ARROW=32512）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    /// <summary>
    /// Win10 1703+：设置进程 DPI 感知上下文。必须在创建任何窗口之前调用。
    /// 传 <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4</c> 可关闭 DWM 位图拉伸虚拟化。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Win32 WNDCLASSEXW（Unicode）。lpfnWndProc 以函数指针传入，字符串成员以 IntPtr 传入（调用方分配后释放）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        /// <summary>结构体字节数（Marshal.SizeOf 取）。</summary>
        public uint cbSize;
        /// <summary>窗口类样式（CS_OWNDC=0x0020：WGL 上下文要求窗口私有 DC）。</summary>
        public uint style;
        /// <summary>窗口过程函数指针（委托须保持根引用防 GC）。</summary>
        public IntPtr lpfnWndProc;
        /// <summary>类额外字节数。</summary>
        public int cbClsExtra;
        /// <summary>窗口额外字节数。</summary>
        public int cbWndExtra;
        /// <summary>实例句柄。</summary>
        public IntPtr hInstance;
        /// <summary>类图标。</summary>
        public IntPtr hIcon;
        /// <summary>类光标。</summary>
        public IntPtr hCursor;
        /// <summary>背景刷。</summary>
        public IntPtr hbrBackground;
        /// <summary>菜单名。</summary>
        public IntPtr lpszMenuName;
        /// <summary>类名指针。</summary>
        public IntPtr lpszClassName;
        /// <summary>小图标。</summary>
        public IntPtr hIconSm;
    }

    /// <summary>Win32 RECT（左/上/右/下）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>左边界。</summary>
        public int Left;
        /// <summary>上边界。</summary>
        public int Top;
        /// <summary>右边界。</summary>
        public int Right;
        /// <summary>下边界。</summary>
        public int Bottom;
    }

    /// <summary>Win32 MSG（消息泵单元）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>目标窗口。</summary>
        public IntPtr hwnd;
        /// <summary>消息 ID。</summary>
        public int message;
        /// <summary>消息参数 1。</summary>
        public IntPtr wParam;
        /// <summary>消息参数 2。</summary>
        public IntPtr lParam;
        /// <summary>投递时间。</summary>
        public int time;
        /// <summary>投递时鼠标位置。</summary>
        public POINT pt;
    }

    /// <summary>Win32 POINT。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>X 坐标。</summary>
        public int x;
        /// <summary>Y 坐标。</summary>
        public int y;
    }
}