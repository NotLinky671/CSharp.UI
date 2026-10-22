using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WndClassW
{
    public uint Style;
    public IntPtr WndProc;
    public int ClsExtra;
    public int WndExtra;
    public IntPtr Instance;
    public IntPtr Icon;
    public IntPtr Cursor;
    public IntPtr Background;
    public string? MenuName;
    public string ClassName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinMsg
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public TsfPoint Point;
    public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaintStruct
{
    public IntPtr Hdc;
    public int Erase;
    public TsfRect Paint;
    public int Restore;
    public int IncUpdate;
    public int Reserved0;
    public int Reserved1;
    public int Reserved2;
    public int Reserved3;
    public int Reserved4;
    public int Reserved5;
    public int Reserved6;
    public int Reserved7;
}

internal static class WindowNative
{
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_MOVE = 0x0003;
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_SETFOCUS = 0x0007;
    internal const uint WM_KILLFOCUS = 0x0008;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_PAINT = 0x000F;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_DPICHANGED = 0x02E0;

    internal const uint WM_APP_INVOKE = 0x8001;
    internal const uint WM_APP_RELAYOUT = 0x8002;

    internal const uint PM_REMOVE = 0x0001;

    internal const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int IDC_ARROW = 32512;
    /// <summary>R34 overlay：WS_EX_NOACTIVATE——child 只绘制，绝不抢占键盘焦点。</summary>
    internal const uint WS_EX_NOACTIVATE = 0x08000000;

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out WinMsg lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out WinMsg lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref WinMsg lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(ref WinMsg lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(IntPtr hWnd, ref PaintStruct lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>
    /// 真实 child 滚动平移的 SWP 标志：NOSIZE|NOZORDER|NOACTIVATE（0x15）。
    /// 滚动必须真实移动 child，故**不含** NOMOVE；此前误把 NOMOVE 拼入导致 input 不随滚动位移。
    /// </summary>
    internal const uint SwpChildMove = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;

    /// <summary>移动 child 窗口而不改尺寸/Z 序/激活；失败由调用方如实记录，不合成假坐标。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>R29B 显示器证据：返回包含/最接近指定窗口的 HMONITOR。</summary>
    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>MONITORINFO.rcMonitor/rcWork 用现有 TsfRect 布局（l,t,r,b int32）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoW
    {
        public int CbSize;
        public TsfRect RcMonitor;
        public TsfRect RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoW lpmi);
}

/// <summary>
/// 自有 Win32/GDI 窗口：消息循环、按键路由、滚动/移动/缩放、稳定布局发布与绘制。
/// 与生产 OpenTK/Skia 宿主刻意隔离；所有文档/TSF 操作都在本窗口线程（UI 线程）执行。
/// </summary>
internal sealed class TsfHostWindow : IDisposable
{
    private const int TextPaddingX = 16;
    private const int TextPaddingY = 14;
    private const int CaretWidth = 1;

    private static readonly string ClassName = $"CSharp.UI.TsfHostCheck.{Environment.ProcessId}";

    private readonly ProbeLog _log;
    private readonly TsfTrace _trace;
    private readonly AcpTextStore _store;
    private readonly ConcurrentQueue<Invocation> _invocations = new();

    private WindowNative.WndProcDelegate? _wndProc;
    private IntPtr _hwnd;
    private IntPtr _secondHwnd;
    private IntPtr _instance;
    private bool _classRegistered;
    private GdiFont? _font;
    /// <summary>
    /// R36：候选 overlay 的<b>独立</b>字体（14 逻辑 px，DPI 144 → 21 物理 px）。
    /// 只在 host-drawn overlay 模式创建；测量（矩形/页脚宽）与绘制（TextOutW）共用
    /// 本实例。DPI 变化时重建，Dispose 与所有失败回滚路径都释放。
    /// </summary>
    private GdiFont? _candidateFont;
    private AcpLayoutSnapshot? _lastLayout;
    private TsfRect _clientScreen;
    // R29A：绘制坐标映射的基准 —— 真实输入 surface 客户区左上角（屏幕坐标），
    // 与 clip（_clientScreen）解耦。glyph/caret/underline 一律从这里换算。
    private NativeMethods.POINT _surfaceOriginScreen;
    private int _scrollX;
    private int _scrollY;
    private uint _dpi = 96;
    private uint _uiThreadId;
    private bool _created;
    private bool _closing;
    private int _closeRequested;
    private int _closeRequestCount;
    private TsfSessionHost? _session;

    // R29A opt-in 真实输入 surface：viewport child 承载真实裁剪边界，input child 是 TSF
    // GetWnd/GetScreenExt/绘制/键盘/CHAR/焦点所见的真实单行窗口。默认 off 时两值均保持
    // IntPtr.Zero，root 仍是输入 surface，旧行为完全不变。
    private bool _childInputSurface;
    /// <summary>R34D：显式 host-drawn-candidates 模式才创建候选 overlay（默认 off 无新窗口）。</summary>
    private bool _hostDrawnOverlay;
    private IntPtr _viewportHwnd;
    private IntPtr _inputHwnd;
    private bool _inputFocusInitialized;
    private string? _inputFocusResult;
    // R29B：真实输入 surface 收到的 WM_SETFOCUS/WM_KILLFOCUS 事件总数（单调）。
    // 只随真实窗口消息路由递增；SetFocus 被拒/无消息不会伪造计数。焦点短暂离开再回来
    // 前后计数不同——这是 focus==own 相等也无法检测的不连续。只在 UI 线程读写。
    private int _inputFocusEventCount;

    internal TsfHostWindow(AcpTextStore store, ProbeLog log, TsfTrace trace)
    {
        _store = store;
        _log = log;
        _trace = trace;
    }

    internal IntPtr Hwnd => _hwnd;

    internal uint UiThreadId => _uiThreadId;

    /// <summary>消息循环已开始；测试据此确定可以安全 Invoke。</summary>
    internal ManualResetEventSlim LoopStarted { get; } = new(false);

    /// <summary>当前窗口真实 DPI（来自 GetDpiForWindow，不硬编码）。</summary>
    internal uint CurrentDpi => _dpi;

    internal AcpLayoutSnapshot? LastLayout => _lastLayout;

    internal int ScrollY => _scrollY;

    internal int ScrollX => _scrollX;

    /// <summary>窗口句柄是否仍然存在（已销毁/未创建为 false）。</summary>
    internal bool IsAlive => _hwnd != IntPtr.Zero;

    /// <summary>窗口类当前是否处于已注册状态。</summary>
    internal bool IsClassRegistered => _classRegistered;

    /// <summary>opt-in 后 TSF/绘制/键盘/焦点所见的真实输入 hwnd；默认 off 返回 root。</summary>
    internal IntPtr InputHwnd => _childInputSurface && _inputHwnd != IntPtr.Zero ? _inputHwnd : _hwnd;

    /// <summary>opt-in 后真实裁剪 viewport child；默认 off 为 0（不存在）。</summary>
    internal IntPtr ViewportHwnd => _childInputSurface ? _viewportHwnd : IntPtr.Zero;

    /// <summary>
    /// R29B：真实输入 surface 已处理的 WM_SETFOCUS+WM_KILLFOCUS 事件总数（单调）。
    /// 必须在 UI 线程读取（经 <see cref="Invoke"/> 投递或本身在 UI 线程）；
    /// 焦点瞬时离开再返回前后计数不等，用于安全点基线拒绝“away-then-back”。
    /// </summary>
    internal int InputFocusEventCount => _inputFocusEventCount;

    /// <summary>最近一次 <see cref="InitializeInputFocus"/> 的真实 outcome（refused/ok/focus-lost 等）。</summary>
    internal string InputFocusInitOutcome => _inputFocusResult ?? "(not-run)";

    /// <summary>绘制/键盘/CHAR/焦点归属的真实 surface：opt-in 为 input，默认 off 为 root。</summary>
    private IntPtr SurfaceHwnd => _childInputSurface && _inputHwnd != IntPtr.Zero ? _inputHwnd : _hwnd;

    /// <summary>显示边界祖先：opt-in 为 viewport child，默认 off 为 root 自身。</summary>
    private IntPtr ClipAncestorHwnd => _childInputSurface && _viewportHwnd != IntPtr.Zero ? _viewportHwnd : _hwnd;

    // ---- R34：自有候选 overlay（root 客户区内 no-activate child）----
    private IntPtr _overlayHwnd;
    /// <summary>R34E：overlay 展示决策/状态的唯一真实核（与测试共用同一实现）。</summary>
    private readonly OverlayPresentation _overlay = new();

    /// <summary>overlay 是否已创建真实 HWND（UI 线程内查询）。</summary>
    internal bool OverlayAlive => _overlayHwnd != IntPtr.Zero;

    /// <summary>overlay 当前应用的快照（无则 null；只读）。</summary>
    internal CandidateSnapshot? OverlaySnapshot => _overlay.Live;

    /// <summary>overlay 因 input 全裁剪而暂时隐藏（快照仍保留）。</summary>
    internal bool OverlayHiddenByClip => _overlay.HiddenByClip;

    /// <summary>R34：一次真实 overlay 矩形/可见性事实包（供 Inspect 返回）。</summary>
    private readonly record struct OverlayFacts(
        bool HwndOk, TsfRect RootClientScreen, TsfRect InputVisible,
        TsfRect RowScreen, TsfRect Rect, bool WantsShow, string? Failure = null);

    /// <summary>
    /// 触发真实输入 surface 的重绘。opt-in → input child（它才是画 glyph 的窗口）；
    /// 默认 off → root。绝不在 opt-in 下只 InvalidateRect(root) —— 那样真实 surface 不会收到 WM_PAINT。
    /// </summary>
    private void InvalidateInputSurface()
    {
        WindowNative.InvalidateRect(SurfaceHwnd, IntPtr.Zero, false);
    }

    /// <summary>
    /// R29A 消息角色判定（共享、托管可测）：某个 hwnd 是否承载**真实输入**（键盘/CHAR/焦点）。
    /// 默认 off（无 child）：root 自身即输入 surface → true。opt-in：仅真实 input child → true；
    /// root/viewport 只画背景，不冒充输入。
    /// </summary>
    internal static bool HwndRoutesInput(bool childInputSurface, IntPtr hWnd,
        IntPtr rootHwnd, IntPtr inputHwnd)
        => childInputSurface ? (hWnd == inputHwnd && inputHwnd != IntPtr.Zero) : hWnd == rootHwnd;

    /// <summary>R29A 初始聚焦前置条件（托管可测）：root 前台、input 与 root 同进程同 UI 线程才允许。</summary>
    internal static bool CanInitializeInputFocus(IntPtr foregroundHwnd, IntPtr rootHwnd,
        IntPtr inputHwnd, uint rootThreadId, uint rootProcessId, uint inputThreadId, uint inputProcessId)
        => foregroundHwnd != IntPtr.Zero
            && foregroundHwnd == rootHwnd
            && inputHwnd != IntPtr.Zero
            && rootThreadId != 0
            && inputThreadId == rootThreadId
            && inputProcessId == rootProcessId;

    /// <summary>
    /// 初始聚焦一次 UI 派发内的决策/动作结果（托管可测）。<see cref="Initialized"/> 只在
    /// SetFocus 之后 GetFocus 确认 input 才为 true；<see cref="SetFocusCalls"/> 记录真实调用
    /// 次数（拒绝路径必须为 0，重复路径也必须为 0）。
    /// </summary>
    internal readonly struct FocusInitResult
    {
        internal FocusInitResult(bool ok, string outcome, int setFocusCalls, uint threadId)
        {
            Initialized = ok;
            Outcome = outcome;
            SetFocusCalls = setFocusCalls;
            ThreadId = threadId;
        }

        internal bool Initialized { get; }
        internal string Outcome { get; }
        internal int SetFocusCalls { get; }
        internal uint ThreadId { get; }
    }

    /// <summary>
    /// 初始聚焦决策+动作的**单一可注入操作**。本方法被 <see cref="InitializeInputFocus"/> 在
    /// **同一次** UI 线程派发的 body 内调用一次：先读所有原生事实、用 <see cref="CanInitializeInputFocus"/>
    /// 判前置、再决定是否 SetFocus、最后用 GetFocus 确认。把"查 + 设 + 确认"封装成一个可测委托的输入，
    /// 使测试能验证真实执行顺序（拒绝 → 0 次 SetFocus；重复 → 0 次；SetFocus 失败 → 不标初始化）。
    /// </summary>
    /// <param name="alreadyInitialized">是否已成功初始化过（重复路径只读 GetFocus，不再 SetFocus）。</param>
    /// <param name="rootHwnd">root 窗口句柄。</param>
    /// <param name="inputHwnd">真实输入 child 句柄。</param>
    /// <param name="uiThreadId">期望的 owning UI 线程 ID。</param>
    /// <param name="expectedProcessId">期望的拥有者进程 ID。</param>
    /// <param name="probe">注入的原生事实读取器；返回 (foreground, isRoot, isInput, rootTid, rootPid, inputTid, inputPid, focus)。</param>
    /// <param name="setFocus">注入的 SetFocus 执行器；返回是否成功（true=已请求焦点）。</param>
    /// <param name="getFocus">注入的当前 GetFocus 值读取器。</param>
    /// <param name="getThreadId">注入的当前 Win32 线程读取器（生产为 <see cref="NativeMethods.CurrentWin32ThreadId"/>）。</param>
    internal static FocusInitResult RunFocusInitialization(
        bool alreadyInitialized, IntPtr rootHwnd, IntPtr inputHwnd,
        uint uiThreadId, int expectedProcessId,
        Func<(IntPtr foreground, bool rootValid, bool inputValid,
              uint rootTid, uint rootPid, uint inputTid, uint inputPid)> probe,
        Func<IntPtr, bool> setFocus,
        Func<IntPtr> getFocus,
        Func<uint> getThreadId)
    {
        var threadId = getThreadId();

        // 必须在 owning UI 线程上执行；否则拒焦（fail closed）。
        if (threadId != uiThreadId || uiThreadId == 0)
        {
            return new FocusInitResult(false, "wrong-ui-thread", 0, threadId);
        }

        var facts = probe();
        if (!facts.rootValid || !facts.inputValid)
        {
            return new FocusInitResult(false, "invalid-hwnd", 0, threadId);
        }

        if (facts.rootPid != expectedProcessId || facts.inputPid != expectedProcessId)
        {
            return new FocusInitResult(false, "foreign-process", 0, threadId);
        }

        if (facts.rootTid != uiThreadId || facts.inputTid != uiThreadId)
        {
            return new FocusInitResult(false, "foreign-ui-thread", 0, threadId);
        }

        if (!CanInitializeInputFocus(facts.foreground, rootHwnd, inputHwnd,
                facts.rootTid, facts.rootPid, facts.inputTid, facts.inputPid))
        {
            return new FocusInitResult(false, "refused:foreground-or-thread", 0, threadId);
        }

        if (alreadyInitialized)
        {
            // 只初始化一次；之后如实回报真实焦点，绝不再 SetFocus（不夺焦）。
            var focused = getFocus() == inputHwnd;
            return new FocusInitResult(focused, focused ? "ok" : "focus-lost", 0, threadId);
        }

        // 首次：在同一次 UI 派发内立即 SetFocus，随后 GetFocus 确认。
        setFocus(inputHwnd);
        var confirmed = getFocus() == inputHwnd;
        return new FocusInitResult(confirmed, confirmed ? "ok" : "set-focus-mismatch", 1, threadId);
    }

    /// <summary>本实例调用 ShowWindow 的次数：隐藏宿主回归据此证明“绝不显示窗口”。</summary>
    internal int ShowWindowCalls { get; private set; }

    internal bool Create(int width, int height, string title, bool show = true,
        bool childInputSurface = false, bool hostDrawnCandidates = false)
    {
        _uiThreadId = NativeMethods.CurrentWin32ThreadId();
        _childInputSurface = childInputSurface;
        _hostDrawnOverlay = hostDrawnCandidates;
        _wndProc = WndProc;
        var instance = WindowNative.GetModuleHandleW(null);
        _instance = instance;

        var wc = new WndClassW
        {
            Style = 0,
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            Cursor = WindowNative.LoadCursorW(IntPtr.Zero, new IntPtr(WindowNative.IDC_ARROW)),
            Background = IntPtr.Zero,
            ClassName = ClassName,
        };

        var atom = WindowNative.RegisterClassW(ref wc);
        if (atom == 0)
        {
            _trace("window", "register-class-failed", new Dictionary<string, object?>
            {
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return false;
        }

        _classRegistered = true;

        _hwnd = WindowNative.CreateWindowExW(
            0, ClassName, title, WindowNative.WS_OVERLAPPEDWINDOW,
            80, 80, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _trace("window", "create-failed", new Dictionary<string, object?>
            {
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return false;
        }

        _dpi = NativeMethods.GetDpiForWindow(_hwnd);
        var fontPixels = DpiUnits.FontPixelHeight(_dpi);
        _font = GdiFont.TryCreate("Microsoft YaHei UI", fontPixels);

        if (_childInputSurface && !CreateInputSurfaceChildren())
        {
            _trace("window", "child-input-surface-failed", new Dictionary<string, object?>
            {
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            _font?.Dispose();
            _font = null;
            WindowNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            return false;
        }

        // R34D：自有候选 overlay——root 客户区内 WS_EX_NOACTIVATE child。只在
        // --child-input-surface + --host-drawn-candidates 同时启用时创建；默认 off
        // 绝不新增窗口。创建失败使本模式启动如实失败（overlay 是候选唯一展示通道），
        // 回滚 input/viewport/root——绝不把无 overlay 的运行当成成功启动。
        if (_hostDrawnOverlay && _childInputSurface)
        {
            _overlayHwnd = WindowNative.CreateWindowExW(
                WindowNative.WS_EX_NOACTIVATE, ClassName, "CSharp.UI TSF candidates",
                WindowNative.WS_CHILD, 0, 0, 0, 0, _hwnd, IntPtr.Zero, _instance, IntPtr.Zero);
            _trace("window", "overlay-create", new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{_overlayHwnd.ToInt64():X}",
                ["ok"] = _overlayHwnd != IntPtr.Zero,
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            if (_overlayHwnd == IntPtr.Zero)
            {
                if (_inputHwnd != IntPtr.Zero)
                {
                    WindowNative.DestroyWindow(_inputHwnd);
                    _inputHwnd = IntPtr.Zero;
                }
                if (_viewportHwnd != IntPtr.Zero)
                {
                    WindowNative.DestroyWindow(_viewportHwnd);
                    _viewportHwnd = IntPtr.Zero;
                }

                _font?.Dispose();
                _font = null;
                WindowNative.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return false;
            }

            // R36：候选独立字号——只在本模式创建；失败使本模式启动如实失败
            // （没有可测量的候选字体就无法算出真实矩形，绝不退回输入字号硬摆）。
            if (!CreateCandidateFont("create"))
            {
                if (_inputHwnd != IntPtr.Zero)
                {
                    WindowNative.DestroyWindow(_inputHwnd);
                    _inputHwnd = IntPtr.Zero;
                }
                if (_viewportHwnd != IntPtr.Zero)
                {
                    WindowNative.DestroyWindow(_viewportHwnd);
                    _viewportHwnd = IntPtr.Zero;
                }

                WindowNative.DestroyWindow(_overlayHwnd);
                _overlayHwnd = IntPtr.Zero;
                _font?.Dispose();
                _font = null;
                WindowNative.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return false;
            }
        }
        else
        {
            // 默认 off / 未启用本模式：不创建任何候选窗口，也不发额外查询。
            _overlayHwnd = IntPtr.Zero;
        }

        // store.GetWnd/GetScreenExt 见到的真实输入 surface：opt-in 为 input child，默认 root。
        _store.AttachWindow(InputHwnd);
        if (_childInputSurface)
        {
            // GetScreenExt 走无锁真实 input∩viewport 交集，不再用整窗客户区冒充。
            var input = _inputHwnd;
            var ancestor = ClipAncestorHwnd;
            _store.DisplaySurfaceProvider = () => DisplaySurface.FromChildIntersection(input, ancestor);
        }

        _created = true;

        // CreateWindowEx 期间的 WM_SIZE 早于字体创建；这里补一次初始布局，避免首帧空白。
        BuildAndPublishLayout("create");

        _trace("window", "created", new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
            ["dpi"] = _dpi,
            ["thread_awareness"] = DpiNative.DescribeThreadAwareness(),
            ["process_awareness"] = DpiNative.DescribeProcessAwareness(),
            ["per_monitor_v2"] = DpiNative.IsPerMonitorV2(),
            ["font_family"] = _font?.Family ?? "(none)",
            ["font_row_height"] = _font?.RowHeight ?? 0,
            ["font_ascent"] = _font?.Ascent ?? 0,
            ["ui_thread_id"] = _uiThreadId,
            ["visible"] = show,
        });

        if (show)
        {
            ShowWindowCalls++;
            NativeMethods.ShowWindow(_hwnd, WindowNative.SW_SHOW);
            WindowNative.UpdateWindow(_hwnd);
        }

        return true;
    }

    /// <summary>
    /// R29A opt-in：在 root 下创建固定 viewport child 与真实单行 input child（同进程同 UI 线程，
    /// 复用同一 WndProc）。input child 尺寸 = 真实行高 + 上下 padding；隐藏创建绝不 ShowWindow/取焦点。
    /// </summary>
    private bool CreateInputSurfaceChildren()
    {
        var rowHeight = _font?.RowHeight ?? 20;
        var inputHeight = rowHeight + TextPaddingY * 2;

        // viewport child：真实裁剪边界，充满 root 客户区。
        _viewportHwnd = WindowNative.CreateWindowExW(
            0, ClassName, "CSharp.UI TSF viewport",
            WindowNative.WS_CHILD | WindowNative.WS_VISIBLE,
            0, 0, 0, 0, _hwnd, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_viewportHwnd == IntPtr.Zero)
        {
            _trace("window", "viewport-create-failed", new Dictionary<string, object?>
            {
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return false;
        }

        // input child：真实单行输入 surface，绘制/键盘/CHAR/焦点归属此处。
        _inputHwnd = WindowNative.CreateWindowExW(
            0, ClassName, "CSharp.UI TSF input",
            WindowNative.WS_CHILD | WindowNative.WS_VISIBLE,
            TextPaddingX, 0, 0, inputHeight, _viewportHwnd, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_inputHwnd == IntPtr.Zero)
        {
            _trace("window", "input-create-failed", new Dictionary<string, object?>
            {
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            WindowNative.DestroyWindow(_viewportHwnd);
            _viewportHwnd = IntPtr.Zero;
            return false;
        }

        // 初次布局由 BuildAndPublishLayout("create") 完成；这里只需按当前 root 客户区定位两 child。
        if (!NativeMethods.GetClientRect(_hwnd, out var rootClient))
        {
            return true; // 尺寸后续由首个 WM_SIZE/BuildAndPublishLayout 校正。
        }

        var flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        var width = Math.Max(1, rootClient.Width - TextPaddingX * 2);
        if (!WindowNative.SetWindowPos(_viewportHwnd, IntPtr.Zero, 0, 0,
                rootClient.Width, rootClient.Height, flags))
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "create-viewport-position",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
        }
        if (!WindowNative.SetWindowPos(_inputHwnd, IntPtr.Zero, TextPaddingX, -_scrollY,
                width, inputHeight, flags))
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "create-input-position",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
        }

        _trace("window", "input-surface-created", new Dictionary<string, object?>
        {
            ["viewport"] = $"0x{_viewportHwnd.ToInt64():X}",
            ["input"] = $"0x{_inputHwnd.ToInt64():X}",
            ["input_height"] = inputHeight,
            ["row_height"] = rowHeight,
            ["same_ui_thread"] = true,
        });
        return true;
    }

    /// <summary>
    /// R29A opt-in：root 尺寸/DPI 变化后把两 child 重新归位 —— viewport 充满 root 客户区，
    /// input 保持单行高并按当前 scroll 平移。失败如实记 trace，绝不合成假几何。
    /// 默认 off（无 child）时为空操作，旧行为完全不变。
    /// </summary>
    private void RepositionInputSurfaceChildren()
    {
        if (!_childInputSurface || _viewportHwnd == IntPtr.Zero || _inputHwnd == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.GetClientRect(_hwnd, out var rootClient))
        {
            _trace("window", "input-surface-reposition-failed", new Dictionary<string, object?>
            {
                ["reason"] = "get-client-rect-failed",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return;
        }

        var inputHeight = InputSurfaceHeight();
        var width = Math.Max(1, rootClient.Width - TextPaddingX * 2);
        var flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        if (!WindowNative.SetWindowPos(_viewportHwnd, IntPtr.Zero, 0, 0,
                rootClient.Width, rootClient.Height, flags))
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "reposition-viewport",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
        }
        if (!WindowNative.SetWindowPos(_inputHwnd, IntPtr.Zero,
                TextPaddingX - _scrollX, -_scrollY, width, inputHeight, flags))
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "reposition-input",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
        }
    }

    /// <summary>
    /// R36：重建候选字体（先释放旧实例，绝不泄漏 GDI 对象）。只有 host-drawn
    /// overlay 模式才会创建；默认 off 下本方法不创建任何字体资源。
    /// 返回 false 表示原生字体创建失败——调用方必须如实失败，不退回输入字号。
    /// </summary>
    private bool CreateCandidateFont(string reason)
    {
        if (!_hostDrawnOverlay || !_childInputSurface)
        {
            return true;
        }

        _candidateFont?.Dispose();
        _candidateFont = null;
        _candidateFont = GdiFont.TryCreate("Microsoft YaHei UI",
            DpiUnits.CandidateFontPixelHeight(_dpi));
        if (_candidateFont is null)
        {
            _trace("window", "candidate-font-create-failed", new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["dpi"] = _dpi,
                ["pixel_height"] = DpiUnits.CandidateFontPixelHeight(_dpi),
            });
            return false;
        }

        _trace("window", "candidate-font", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["dpi"] = _dpi,
            ["pixel_height"] = DpiUnits.CandidateFontPixelHeight(_dpi),
            ["family"] = _candidateFont.Family,
            ["row_height"] = _candidateFont.RowHeight,
            ["ascent"] = _candidateFont.Ascent,
        });
        return true;
    }

    /// <summary>真实单行 input child 高度 = 行高 + 上下 padding（纯几何，托管可测）。</summary>
    private int InputSurfaceHeight() => (_font?.RowHeight ?? 20) + TextPaddingY * 2;

    /// <summary>
    /// R29A 一次性初始聚焦：仅当 root 已在前台且 input 与 root 同进程同 UI 线程时，
    /// 才从 UI 线程 SetFocus(input)。否则如实拒绝，绝不 AttachThreadInput/TOPMOST/IME-HWND 操作。
    /// 绝不从 scroll/layout/paint/geometry 回调触发。
    /// </summary>
    internal bool InitializeInputFocus()
        => InitializeInputFocus(RunFocusInitialization);

    /// <summary>
    /// 初始聚焦的可注入实现：<paramref name="operation"/> 在**恰好一次** UI 线程派发内执行，
    /// 完成"读原生事实 → 判前置 → 可选 SetFocus → GetFocus 确认"全过程。默认实参是
    /// <see cref="RunFocusInitialization"/>，测试可注入桩验证决策/动作顺序而无需真实窗口。
    /// 绝不重试夺焦；调用方/测试须保证本函数只在需要初始聚焦的显式路径上被调用。
    /// </summary>
    internal bool InitializeInputFocus(
        Func<bool, IntPtr, IntPtr, uint, int,
            Func<(IntPtr, bool, bool, uint, uint, uint, uint)>,
            Func<IntPtr, bool>, Func<IntPtr>, Func<uint>, FocusInitResult> operation)
    {
        if (!_childInputSurface || _inputHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        // 恰好一次 UI 派发：读 _inputFocusInitialized、决定+SetFocus+确认、写回结果与
        // _inputFocusInitialized 全在 owning UI 线程 body 内完成。另一个排队初始化在 UI 线程上
        // 只可能在 body 已写回后才读取状态——绝不在 worker 上后置赋值造成"已 SetFocus 仍见 false"。
        // 无 dispatcher（未建窗口/未起循环）则 Invoke 退化为直接执行——body 内仍会校验线程归属，
        // fail closed。绝不在调用线程上先读 GetFocus/foreground 再二次 Invoke（race）。
        return Invoke(() =>
        {
            var result = operation(
                _inputFocusInitialized, _hwnd, _inputHwnd, _uiThreadId, Environment.ProcessId,
                () =>
                {
                    var foreground = NativeMethods.GetForegroundWindow();
                    var rootValid = NativeMethods.IsWindow(_hwnd);
                    var inputValid = NativeMethods.IsWindow(_inputHwnd);
                    var rootTid = NativeMethods.GetWindowThreadProcessId(_hwnd, out var rootPid);
                    var inputTid = NativeMethods.GetWindowThreadProcessId(_inputHwnd, out var inputPid);
                    return (foreground, rootValid, inputValid, rootTid, rootPid, inputTid, inputPid);
                },
                h => WindowNative.SetFocus(h) != IntPtr.Zero || NativeMethods.GetFocus() == h,
                () => NativeMethods.GetFocus(),
                NativeMethods.CurrentWin32ThreadId);

            // 初始化状态的全部读/写都在此 UI body 内；worker 不碰 _inputFocusInitialized/_inputFocusResult。
            _inputFocusResult = result.Outcome;
            if (result.Initialized)
            {
                _inputFocusInitialized = true;
                _trace("window", "input-focus-initialized", new Dictionary<string, object?>
                {
                    ["hwnd"] = $"0x{_inputHwnd.ToInt64():X}",
                    ["thread_id"] = result.ThreadId,
                    ["outcome"] = result.Outcome,
                });
            }
            else
            {
                _trace("window", "input-focus-refused", new Dictionary<string, object?>
                {
                    ["root"] = $"0x{_hwnd.ToInt64():X}",
                    ["input"] = $"0x{_inputHwnd.ToInt64():X}",
                    ["thread_id"] = result.ThreadId,
                    ["outcome"] = result.Outcome,
                    ["set_focus_calls"] = result.SetFocusCalls,
                });
            }

            return result.Initialized;
        });
    }

    internal void AttachSession(TsfSessionHost session) => _session = session;

    internal int RunMessageLoop()
    {
        LoopStarted.Set();
        var exitCode = 0;
        while (true)
        {
            var result = WindowNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (result == 0)
            {
                exitCode = (int)msg.WParam;
                break;
            }

            if (result < 0)
            {
                exitCode = 1;
                break;
            }

            WindowNative.TranslateMessage(ref msg);
            WindowNative.DispatchMessageW(ref msg);
        }

        return exitCode;
    }

    /// <summary>在 UI 线程同步执行；从 UI 线程调用时直接执行。</summary>
    internal void Invoke(Action action, int timeoutMs = 15000)
    {
        if (!_created)
        {
            action();
            return;
        }

        if (NativeMethods.CurrentWin32ThreadId() == _uiThreadId)
        {
            action();
            return;
        }

        var invocation = new Invocation(action);
        _invocations.Enqueue(invocation);
        if (!NativeMethods.PostMessage(_hwnd, WindowNative.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("PostMessage(WM_APP_INVOKE) failed; window may be destroyed.");
        }

        if (!invocation.Done.Wait(timeoutMs))
        {
            throw new TimeoutException($"UI invocation did not complete within {timeoutMs}ms.");
        }

        if (invocation.Error is { } error)
        {
            throw new AggregateException(error);
        }
    }

    /// <summary>在 UI 线程同步执行并返回结果。</summary>
    internal T Invoke<T>(Func<T> func, int timeoutMs = 15000)
    {
        var result = default(T)!;
        // 必须显式用不返回值的 Action 块 lambda，否则赋值表达式 lambda 仍返回 T，
        // 重载解析会再次选择 Invoke<T> 并无限递归（R11 真实栈溢出）。
        Action action = () => { result = func(); };
        Invoke(action, timeoutMs);
        return result;
    }

    /// <summary>投递到 UI 线程，不等待。</summary>
    internal void Post(Action action)
    {
        if (!_created)
        {
            return;
        }

        if (NativeMethods.CurrentWin32ThreadId() == _uiThreadId)
        {
            action();
            return;
        }

        _invocations.Enqueue(new Invocation(action));
        NativeMethods.PostMessage(_hwnd, WindowNative.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>始终投递到消息队列（即使当前就在 UI 线程），用于必须在调用栈返回后执行的通知。</summary>
    internal void PostAlways(Action action)
    {
        if (!_created)
        {
            return;
        }

        _invocations.Enqueue(new Invocation(action));
        NativeMethods.PostMessage(_hwnd, WindowNative.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 请求重新布局。只入队并投递 WM_APP_INVOKE，绝不在 TSF store/sink 回调内同步排版；
    /// 即使从 UI 线程调用也走消息队列。
    /// </summary>
    internal void RequestRelayout(string reason)
    {
        PostAlways(() =>
        {
            if (_closing)
            {
                return;
            }

            _store.InvalidateLayout(reason);
            BuildAndPublishLayout(reason);
            InvalidateInputSurface();
        });
    }

    /// <summary>
    /// 自有 HWND 归属门禁：操作（滚动/移动/恢复）只允许落在仍属本进程、本 UI 线程的
    /// 存活窗口上；外来或已回收的句柄一律拒绝——绝不操作别人的 HWND。
    /// 必须在 UI 线程上下文内调用（Invoke body / WndProc）。
    /// </summary>
    private bool OwnsLiveWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        var tid = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return tid == _uiThreadId && pid == Environment.ProcessId;
    }

    /// <summary>滚动恢复/测量要动的真实 surface：opt-in 为 input child，默认 root。</summary>
    private bool OwnsInputSurface()
        => OwnsLiveWindow(_hwnd)
            && (!_childInputSurface || OwnsLiveWindow(_inputHwnd));

    internal bool ScrollBy(int deltaY) => Invoke(() =>
    {
        if (!OwnsInputSurface())
        {
            return false;
        }

        _scrollY += deltaY;
        MoveInputSurfaceForScroll();
        _store.InvalidateLayout("scroll-y");
        BuildAndPublishLayout("scroll-y");
        InvalidateInputSurface();
        UpdateCandidateOverlayGeometry("scroll-y");
        return true;
    });

    /// <summary>按逻辑单位（96 DPI 基准）滚动，返回实际使用的物理像素；日志记录两者与真实 DPI。</summary>
    internal int ScrollByLogical(int logicalDelta)
    {
        var physical = DpiUnits.LogicalToPhysical(logicalDelta, CurrentDpi);
        Invoke(() =>
        {
            _scrollY += physical;
            MoveInputSurfaceForScroll();
            _store.InvalidateLayout("scroll-y-logical");
            BuildAndPublishLayout("scroll-y-logical");
            InvalidateInputSurface();
            UpdateCandidateOverlayGeometry("scroll-y-logical");
        });
        _trace("window", "scroll-logical", new Dictionary<string, object?>
        {
            ["logical_delta"] = logicalDelta,
            ["physical_delta"] = physical,
            ["dpi"] = CurrentDpi,
            ["unit"] = "logical(96dpi) -> physical screen px",
        });
        return physical;
    }

    internal void ScrollXBy(int deltaX) => Invoke(() =>
    {
        _scrollX += deltaX;
        MoveInputSurfaceForScroll();
        _store.InvalidateLayout("scroll-x");
        BuildAndPublishLayout("scroll-x");
        InvalidateInputSurface();
    });

    internal bool SetScroll(int x, int y) => Invoke(() =>
    {
        if (!OwnsInputSurface())
        {
            return false;
        }

        _scrollX = x;
        _scrollY = y;
        MoveInputSurfaceForScroll();
        _store.InvalidateLayout("scroll-set");
        BuildAndPublishLayout("scroll-set");
        InvalidateInputSurface();
        // R34D：input∩viewport 交集已按新 scroll 变化——重算 overlay 几何；
        // 全裁剪如实只藏展示（live 快照保留），恢复由 restore/下一次发布驱动。
        UpdateCandidateOverlayGeometry("scroll-set");
        return true;
    });

    /// <summary>
    /// R29A：opt-in 时以 SetWindowPos 把真实 input child 平移到 scroll 对应位置（每次滚动移动一次，
    /// SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE）；本地排版不再二次减 scroll。默认 off 不移动任何窗口。
    /// </summary>
    private void MoveInputSurfaceForScroll()
    {
        if (!_childInputSurface || _inputHwnd == IntPtr.Zero)
        {
            return;
        }

        var moved = WindowNative.SetWindowPos(_inputHwnd, IntPtr.Zero,
            TextPaddingX - _scrollX, -_scrollY, 0, 0, WindowNative.SwpChildMove);
        _trace("window", "input-surface-move", new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{_inputHwnd.ToInt64():X}",
            ["x"] = TextPaddingX - _scrollX,
            ["y"] = -_scrollY,
            ["scroll_x"] = _scrollX,
            ["scroll_y"] = _scrollY,
            ["set_window_pos"] = moved,
        });
    }

    internal bool MoveWindowBy(int dx, int dy) => Invoke(() =>
    {
        if (!OwnsLiveWindow(_hwnd) || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        var moved = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, rect.Left + dx, rect.Top + dy, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        if (!moved)
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "window-move",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return false;
        }

        _store.InvalidateLayout("window-move");
        BuildAndPublishLayout("window-move");
        InvalidateInputSurface();
        return true;
    });

    /// <summary>
    /// 把 root 顶层窗口按保存的<b>绝对原点</b>恢复（SWP_NOSIZE|NOZORDER|NOACTIVATE——
    /// 不夺回焦点/前台、不改尺寸/Z 序）。恢复前复核 HWND 仍属本进程本 UI 线程；
    /// 返回 false 表示恢复未执行，绝不谎报成功。
    /// </summary>
    internal bool SetRootWindowOrigin(int x, int y) => Invoke(() =>
    {
        if (!OwnsLiveWindow(_hwnd))
        {
            return false;
        }

        var moved = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        if (!moved)
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "root-origin-restore",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return false;
        }

        _store.InvalidateLayout("root-origin-restore");
        BuildAndPublishLayout("root-origin-restore");
        InvalidateInputSurface();
        return true;
    });

    /// <summary>
    /// R30 opt-in 实验（<c>--child-hide-when-clipped</c>）：对<b>自有 input child</b> 做一次
    /// 真实可见性变更。hide/show 都用 SWP_NOACTIVATE|NOMOVE|NOSIZE|NOZORDER——不夺焦、不激活、
    /// 不改几何/Z 序；操作前后在原生侧读回真实 IsWindowVisible，绝不把调用成功当成
    /// "已隐藏/已显示"（更不当作"候选已隐藏"）。归属校验（OwnsLiveWindow root+input）失败、
    /// 原生调用 false/异常都如实转成失败结果；show 只在 <paramref name="wasOriginallyVisible"/>
    /// 为 true 时提交。必须在 owning UI 线程派发内调用（本方法内部 Invoke 保证）。
    /// 不做 SetFocus、不发 WM_IME_*、不碰任何候选窗口。
    /// </summary>
    /// <param name="showOnlyIfWasVisible">show=true 且本参数为 false 时只读回真实
    /// 状态、不提交原生写（恢复路径不伪造一次显示）。</param>
    internal ChildSurfaceVisibilityResult SetInputSurfaceVisibility(bool show,
        bool showOnlyIfWasVisible)
    {
        var result = default(ChildSurfaceVisibilityResult);
        Invoke(() =>
        {
            result = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(show,
                ownsWindow: () => _childInputSurface
                    && OwnsLiveWindow(_hwnd)
                    && OwnsLiveWindow(_inputHwnd),
                isVisible: () => NativeMethods.IsWindowVisible(_inputHwnd),
                setVisible: visible => NativeMethods.SetWindowPos(_inputHwnd, IntPtr.Zero,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE
                        | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
                        | (visible ? NativeMethods.SWP_SHOWWINDOW : NativeMethods.SWP_HIDEWINDOW)),
                showOnlyIfWasVisible: showOnlyIfWasVisible);
        });
        _trace("window", "input-surface-visibility", new Dictionary<string, object?>
        {
            ["show"] = show,
            ["ok"] = result.Ok,
            ["was_visible"] = result.WasVisible,
            ["visible_after"] = result.VisibleAfter,
            ["reason"] = result.Reason,
        });
        return result;
    }

    /// <summary>
    /// R30：在 owning UI 线程内<b>只读</b>自有 input child 的真实可见性——归属门禁
    /// （OwnsLiveWindow root+input）先过，拒绝时如实回 false；绝不写、不夺焦、不碰
    /// 候选窗口。执行器在 hide 的原生动作之前用本读数单独保存原可见性——"原生
    /// hide 已生效但返回 false/抛异常"时 finally 恢复以本保存值为准。
    /// </summary>
    internal bool ReadInputSurfaceVisibility()
        => _childInputSurface
            && OwnsLiveWindow(_hwnd)
            && OwnsLiveWindow(_inputHwnd)
            && NativeMethods.IsWindowVisible(_inputHwnd);

    internal void ResizeWindow(int width, int height) => Invoke(() =>
    {
        if (!NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
        {
            _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
            {
                ["site"] = "window-resize",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
        }
        _store.InvalidateLayout("window-resize");
        BuildAndPublishLayout("window-resize");
        InvalidateInputSurface();
    });

    /// <summary>创建并聚焦本进程的第二个自有窗口，用于真实 WM_KILLFOCUS（不抢其他进程前台）。</summary>
    internal bool FocusSecondWindow()
    {
        var ok = false;
        Invoke(() =>
        {
            if (_secondHwnd == IntPtr.Zero)
            {
                _secondHwnd = WindowNative.CreateWindowExW(
                    0, ClassName, "CSharp.UI TSF prototype (focus target)",
                    WindowNative.WS_OVERLAPPEDWINDOW,
                    820, 80, 320, 200, IntPtr.Zero, IntPtr.Zero,
                    WindowNative.GetModuleHandleW(null), IntPtr.Zero);
                if (_secondHwnd == IntPtr.Zero)
                {
                    _trace("window", "second-create-failed", new Dictionary<string, object?>
                    {
                        ["win32_error"] = Marshal.GetLastWin32Error(),
                    });
                    return;
                }

                NativeMethods.ShowWindow(_secondHwnd, WindowNative.SW_SHOW);
            }

            WindowNative.SetFocus(_secondHwnd);
            ok = NativeMethods.GetFocus() == _secondHwnd;
            _trace("window", "second-focused", new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{_secondHwnd.ToInt64():X}",
                ["set_focus"] = ok,
                ["focus_now"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
            });
        });

        return ok;
    }

    internal void DestroySecondWindow() => Invoke(() =>
    {
        if (_secondHwnd != IntPtr.Zero)
        {
            WindowNative.DestroyWindow(_secondHwnd);
            _secondHwnd = IntPtr.Zero;
        }
    });

    internal void RefocusMainWindow() => Invoke(() =>
    {
        var previous = WindowNative.SetFocus(_hwnd);
        _trace("window", "main-refocused", new Dictionary<string, object?>
        {
            ["previous_focus"] = $"0x{previous.ToInt64():X}",
            ["focus_now"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
        });
    });

    internal void CloseWindow() => RequestClose();

    /// <summary>只投递一次自有 UI 关闭；已关闭或重复调用不再 post。</summary>
    internal void RequestClose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _closeRequestCount);
        PostAlways(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessage(_hwnd, WindowNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    internal int CloseRequestCount => Volatile.Read(ref _closeRequestCount);

    internal (int X, int Y, int Width, int Height, string Path)? Capture(string path)
        => ScreenCapture.CaptureWindowRegion(_hwnd, path);

    /// <summary>
    /// R29B 显示器级证据：MonitorFromWindow(root, NEAREST) + GetMonitorInfoW 取**真实**监视器
    /// 矩形，再按该矩形 ScreenCapture.CaptureRegion（只读位块传输；不枚举任何候选 HWND）。
    /// 返回实际监视器矩形与文件路径；任一原生查询失败返回 null，绝不合成假坐标。
    /// </summary>
    internal (int X, int Y, int Width, int Height, string Path)? CaptureMonitorRegion(string path)
    {
        (int X, int Y, int Width, int Height, string Path)? result = null;
        Invoke(() =>
        {
            if (!TryGetMonitorRect(out var r))
            {
                return;
            }

            var cap = ScreenCapture.CaptureRegion(r.Left, r.Top, r.Width, r.Height, path);
            if (cap is { } captured)
            {
                result = (r.Left, r.Top, captured.Width, captured.Height, captured.Path);
            }

            _trace("window", "monitor-capture", new Dictionary<string, object?>
            {
                ["monitor_rect"] = r.ToString(),
                ["path"] = cap?.Path ?? "(failed)",
            });
        });
        return result;
    }

    /// <summary>
    /// 在<b>已在 UI 线程</b>的受管 body 内取 root 所在监视器的真实屏幕矩形。
    /// MonitorFromWindow+GetMonitorInfoW 任一失败返回 false——bounds 缺失/无效即拒绝，
    /// 绝不合成假坐标；不枚举候选 HWND。
    /// </summary>
    internal bool TryGetMonitorRect(out TsfRect rect)
    {
        rect = default;
        var hmon = WindowNative.MonitorFromWindow(_hwnd, WindowNative.MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero)
        {
            _trace("window", "monitor-info-failed", new Dictionary<string, object?>
            {
                ["reason"] = "monitor-from-window-null",
            });
            return false;
        }

        var info = new WindowNative.MonitorInfoW
        {
            CbSize = Marshal.SizeOf<WindowNative.MonitorInfoW>(),
        };
        if (!WindowNative.GetMonitorInfoW(hmon, ref info))
        {
            _trace("window", "monitor-info-failed", new Dictionary<string, object?>
            {
                ["hmonitor"] = $"0x{hmon.ToInt64():X}",
                ["win32_error"] = Marshal.GetLastWin32Error(),
            });
            return false;
        }

        var r = info.RcMonitor;
        if (r.Width <= 0 || r.Height <= 0)
        {
            _trace("window", "monitor-info-failed", new Dictionary<string, object?>
            {
                ["hmonitor"] = $"0x{hmon.ToInt64():X}",
                ["reason"] = "empty-monitor-rect",
            });
            return false;
        }

        rect = r;
        return true;
    }

    /// <summary>当前垂直滚动偏移（物理像素；opt-in scroll-follow 精确恢复用）。</summary>
    internal int ScrollOffsetY => Invoke(() => _scrollY);

    /// <summary>当前水平滚动偏移（物理像素）。</summary>
    internal int ScrollOffsetX => Invoke(() => _scrollX);

    /// <summary>
    /// opt-in 真实 input child 的**未裁剪** client 屏幕矩形（GetClientRect+ClientToScreen）。
    /// clip-out 用它与 viewport 顶边直接比较；默认 off 时退化为 root client rect。
    /// </summary>
    internal TsfRect InputSurfaceScreenRect
    {
        get
        {
            var result = default(TsfRect);
            Invoke(() =>
            {
                var hwnd = _childInputSurface && _inputHwnd != IntPtr.Zero ? _inputHwnd : _hwnd;
                if (NativeMethods.GetClientRect(hwnd, out var client))
                {
                    var origin = new NativeMethods.POINT { X = client.Left, Y = client.Top };
                    if (NativeMethods.ClientToScreen(hwnd, ref origin))
                    {
                        result = TsfRect.FromLtrb(origin.X, origin.Y,
                            origin.X + client.Width, origin.Y + client.Height);
                    }
                }
            });
            return result;
        }
    }

    /// <summary>
    /// opt-in clip 祖先（viewport）的 client 屏幕矩形；clip-out 的固定参照。
    /// 默认 off 时退化为 root client rect。
    /// </summary>
    internal TsfRect ViewportClientScreenRect
    {
        get
        {
            var result = default(TsfRect);
            Invoke(() =>
            {
                var hwnd = _childInputSurface && _viewportHwnd != IntPtr.Zero ? _viewportHwnd : _hwnd;
                if (NativeMethods.GetClientRect(hwnd, out var client))
                {
                    var origin = new NativeMethods.POINT { X = client.Left, Y = client.Top };
                    if (NativeMethods.ClientToScreen(hwnd, ref origin))
                    {
                        result = TsfRect.FromLtrb(origin.X, origin.Y,
                            origin.X + client.Width, origin.Y + client.Height);
                    }
                }
            });
            return result;
        }
    }

    internal TsfRect ClientScreenRect
    {
        get
        {
            var result = default(TsfRect);
            Invoke(() =>
            {
                if (_childInputSurface)
                {
                    // opt-in：真实可见区域 = input ∩ 全链裁剪祖先（与发布/TSF 查询同源）。
                    var surface = DisplaySurface.FromChildIntersection(InputHwnd, ClipAncestorHwnd);
                    if (surface.Ok)
                    {
                        result = surface.Rect;
                    }
                    return;
                }

                if (NativeMethods.GetClientRect(_hwnd, out var client))
                {
                    var origin = new NativeMethods.POINT { X = client.Left, Y = client.Top };
                    if (NativeMethods.ClientToScreen(_hwnd, ref origin))
                    {
                        result = TsfRect.FromLtrb(origin.X, origin.Y,
                            origin.X + client.Width, origin.Y + client.Height);
                    }
                }
            });
            return result;
        }
    }

    internal TsfRect TextRowScreenRect
    {
        get
        {
            var result = default(TsfRect);
            Invoke(() =>
            {
                if (_lastLayout is { } layout)
                {
                    result = layout.TextRowScreen;
                }
            });
            return result;
        }
    }

    /// <summary>
    /// root 顶层窗口的真实 GetWindowRect（move-window 绝对恢复用）。
    /// 原生查询失败或 HWND 已不属于本进程时回传默认矩形——执行器对空矩形显式判失败，
    /// 绝不把 default 当成一次成功查询。
    /// </summary>
    internal TsfRect RootWindowScreenRect
    {
        get
        {
            var result = default(TsfRect);
            Invoke(() =>
            {
                if (OwnsLiveWindow(_hwnd) && NativeMethods.GetWindowRect(_hwnd, out var rect))
                {
                    result = TsfRect.FromLtrb(rect.Left, rect.Top, rect.Right, rect.Bottom);
                }
            });
            return result;
        }
    }

    // -------------------------------------------------------------------
    // R29B review-6：显式成败的原始矩形查询（default 不再掩盖查询失败）。
    // 既有 default-rect getter 保留给执行器受管 body（它对空矩形显式判失败）；
    // 下列 Try* 与 TryInputSurfaceScreenRect 供安全点/诊断把"查询失败"如实上抛，
    // 绝不把 stale/zero raw geometry 当成功。
    // -------------------------------------------------------------------

    /// <summary>root 顶层窗口真实 GetWindowRect（显式成败）。</summary>
    internal bool TryGetRootWindowScreenRect(out TsfRect rect)
    {
        var result = default(TsfRect);
        var ok = Invoke(() =>
        {
            if (!OwnsLiveWindow(_hwnd) || !NativeMethods.GetWindowRect(_hwnd, out var r))
            {
                return false;
            }

            result = TsfRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        });
        rect = result;
        return ok;
    }

    /// <summary>
    /// R36：root 的<b>真实客户区</b>屏幕矩形（GetClientRect + ClientToScreen）——
    /// 不含标题栏/边框。候选 overlay 必须收在这个矩形内，而不是 root 外框
    /// （GetWindowRect 含 caption，会多出约 31px 假高度）。
    /// </summary>
    internal bool TryGetRootClientScreenRect(out TsfRect rect)
    {
        var result = default(TsfRect);
        var ok = Invoke(() =>
        {
            if (!OwnsLiveWindow(_hwnd) || !NativeMethods.GetClientRect(_hwnd, out var client))
            {
                return false;
            }

            var origin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(_hwnd, ref origin))
            {
                return false;
            }

            result = TsfRect.FromLtrb(origin.X, origin.Y,
                origin.X + client.Width, origin.Y + client.Height);
            return true;
        });
        rect = result;
        return ok;
    }

    /// <summary>input client 未裁剪屏幕矩形（显式成败；opt-in 为 input child）。</summary>
    internal bool TryGetInputSurfaceScreenRect(out TsfRect rect)
    {
        var result = default(TsfRect);
        var ok = Invoke(() =>
        {
            var hwnd = _childInputSurface && _inputHwnd != IntPtr.Zero ? _inputHwnd : _hwnd;
            if (!NativeMethods.GetClientRect(hwnd, out var client))
            {
                return false;
            }

            var origin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(hwnd, ref origin))
            {
                return false;
            }

            result = TsfRect.FromLtrb(origin.X, origin.Y,
                origin.X + client.Width, origin.Y + client.Height);
            return true;
        });
        rect = result;
        return ok;
    }

    /// <summary>clip 祖先（viewport）client 屏幕矩形（显式成败）。</summary>
    internal bool TryGetViewportClientScreenRect(out TsfRect rect)
    {
        var result = default(TsfRect);
        var ok = Invoke(() =>
        {
            var hwnd = _childInputSurface && _viewportHwnd != IntPtr.Zero ? _viewportHwnd : _hwnd;
            if (!NativeMethods.GetClientRect(hwnd, out var client))
            {
                return false;
            }

            var origin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(hwnd, ref origin))
            {
                return false;
            }

            result = TsfRect.FromLtrb(origin.X, origin.Y,
                origin.X + client.Width, origin.Y + client.Height);
            return true;
        });
        rect = result;
        return ok;
    }

    /// <summary>input ∩ 全链裁剪祖先的真实可见屏幕矩形（显式成败；与 TSF 查询同源）。</summary>
    internal bool TryGetVisibleInputSurfaceRect(out TsfRect rect, out string? failure)
    {
        var result = default(TsfRect);
        string? reason = null;
        var ok = Invoke(() =>
        {
            if (!_childInputSurface)
            {
                return TryGetViewportClientScreenRect(out result);
            }

            var surface = DisplaySurface.FromChildIntersection(InputHwnd, ClipAncestorHwnd);
            if (!surface.Ok)
            {
                reason = surface.Failure ?? "display-surface-query-failed";
                return false;
            }

            result = surface.Rect;
            return true;
        });
        rect = result;
        failure = ok ? null : reason;
        return ok;
    }

    /// <summary>已发布布局的文本行屏幕矩形（显式成败；无布局即失败）。</summary>
    internal bool TryGetTextRowScreenRect(out TsfRect rect)
    {
        var result = default(TsfRect);
        var ok = Invoke(() =>
        {
            if (_lastLayout is not { } layout)
            {
                return false;
            }

            result = layout.TextRowScreen;
            return true;
        });
        rect = result;
        return ok;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 第二个自有窗口只用于制造真实 WM_KILLFOCUS；它的消息全部交给默认过程，
        // 不触碰主窗口的会话、布局或退出消息。
        if (hWnd == _secondHwnd)
        {
            return WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        var isRoot = hWnd == _hwnd;
        var isViewport = _childInputSurface && hWnd == _viewportHwnd;
        var isOverlay = _overlayHwnd != IntPtr.Zero && hWnd == _overlayHwnd;
        // 共享角色判定：默认 off 时 root 即真实输入 surface；opt-in 时仅 input child 路由
        // 键盘/CHAR/焦点，root/viewport 只画背景。
        var routesInput = HwndRoutesInput(_childInputSurface, hWnd, _hwnd, _inputHwnd);

        // opt-in 时 input child 走完整输入路径；viewport/root 只重排+背景。未知 child 一律 DefWindowProc。
        if (!isRoot && !routesInput && !isViewport && !isOverlay)
        {
            return WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        try
        {
            switch (msg)
            {
                case WindowNative.WM_APP_INVOKE:
                    DrainInvocations();
                    return IntPtr.Zero;

                case WindowNative.WM_APP_RELAYOUT:
                    if (isRoot && !_closing)
                    {
                        _store.InvalidateLayout("relayout-message");
                        BuildAndPublishLayout("relayout-message");
                        InvalidateInputSurface();
                    }

                    return IntPtr.Zero;

                case WindowNative.WM_SETFOCUS:
                    // 只有真实输入 surface 获得焦点才上报/计数；默认 off 时 root 即输入 surface。
                    if (routesInput)
                    {
                        _inputFocusEventCount++;
                        _session?.OnWindowFocus(true);
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_KILLFOCUS:
                    if (routesInput)
                    {
                        _inputFocusEventCount++;
                        _session?.OnWindowFocus(false);
                        // R34：焦点丢失即清屏——overlay 绝不越过输入焦点生命周期展示。
                        HideCandidateOverlay("window-focus-lost");
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_KEYDOWN:
                case WindowNative.WM_SYSKEYDOWN:
                    if (routesInput && _session?.RouteKeyDown(wParam, lParam) == true)
                    {
                        return IntPtr.Zero;
                    }

                    break;

                case WindowNative.WM_KEYUP:
                case WindowNative.WM_SYSKEYUP:
                    if (routesInput && _session?.RouteKeyUp(wParam, lParam) == true)
                    {
                        return IntPtr.Zero;
                    }

                    break;

                case WindowNative.WM_CHAR:
                    if (routesInput && _session?.HandleChar(wParam) == true)
                    {
                        return IntPtr.Zero;
                    }

                    break;

                case WindowNative.WM_MOUSEWHEEL:
                    if (routesInput)
                    {
                        HandleWheel(wParam);
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_DPICHANGED:
                    if (isRoot)
                    {
                        HandleDpiChanged(wParam, lParam);
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_SIZE:
                case WindowNative.WM_MOVE:
                    if (isRoot && !_closing)
                    {
                        // root 尺寸变化：先把真实 child 归位（opt-in），再据此发布新布局。
                        if (msg == WindowNative.WM_SIZE)
                        {
                            RepositionInputSurfaceChildren();
                        }
                        _store.InvalidateLayout(msg == WindowNative.WM_SIZE ? "wm-size" : "wm-move");
                        BuildAndPublishLayout(msg == WindowNative.WM_SIZE ? "wm-size" : "wm-move");
                        // R34：root 几何变化——重新按当前快照锚定 overlay（无快照为空操作）。
                        UpdateCandidateOverlayGeometry("wm-" + (msg == WindowNative.WM_SIZE ? "size" : "move"));
                    }

                    break;

                case WindowNative.WM_PAINT:
                    if (isOverlay)
                    {
                        PaintCandidateOverlay(hWnd);
                    }
                    else if (routesInput || isViewport || isRoot)
                    {
                        // 每个 hwnd 都必须 BeginPaint/EndPaint 校验自己的 update region；
                        // 但仅真实输入 surface 画 glyph/selection/caret，root/viewport 只画背景。
                        Paint(hWnd, routesInput);
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_ERASEBKGND:
                    return new IntPtr(1);

                case WindowNative.WM_CLOSE:
                    if (isRoot && !_closing)
                    {
                        _closing = true;
                        _session?.Cleanup();
                        WindowNative.DestroyWindow(hWnd);
                    }

                    return IntPtr.Zero;

                case WindowNative.WM_DESTROY:
                    if (isRoot)
                    {
                        _hwnd = IntPtr.Zero;
                        _inputHwnd = IntPtr.Zero;
                        _viewportHwnd = IntPtr.Zero;
                        _overlayHwnd = IntPtr.Zero;
                        WindowNative.PostQuitMessage(0);
                    }
                    return IntPtr.Zero;
            }

            return WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            _trace("window", "wndproc-exception", new Dictionary<string, object?>
            {
                ["message"] = NativeMethods.MessageName(msg),
                ["exception"] = ex.GetType().Name,
                ["detail"] = ex.Message,
            });
            return WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void DrainInvocations()
    {
        var processed = 0;
        while (processed++ < 64 && _invocations.TryDequeue(out var invocation))
        {
            try
            {
                invocation.Action();
            }
            catch (Exception ex)
            {
                invocation.Error = ex;
            }
            finally
            {
                invocation.Done.Set();
            }
        }

        if (!_invocations.IsEmpty)
        {
            NativeMethods.PostMessage(_hwnd, WindowNative.WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void HandleWheel(IntPtr wParam)
    {
        var delta = (short)((long)wParam >> 16);
        var lines = delta / 120;
        if (lines == 0)
        {
            lines = delta > 0 ? 1 : -1;
        }

        _scrollY += lines * (_font?.RowHeight ?? 20);
        MoveInputSurfaceForScroll();
        _store.InvalidateLayout("wheel-scroll");
        BuildAndPublishLayout("wheel-scroll");
        InvalidateInputSurface();
    }

    private void HandleDpiChanged(IntPtr wParam, IntPtr lParam)
    {
        var newDpi = (uint)((long)wParam & 0xFFFF);
        if (lParam != IntPtr.Zero)
        {
            // 使用系统建议 rect（PMv2 下为真实物理像素），不自行乘系数。
            var suggested = Marshal.PtrToStructure<TsfRect>(lParam);
            if (!NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
                    suggested.Left, suggested.Top, suggested.Width, suggested.Height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
            {
                _trace("window", "set-window-pos-failed", new Dictionary<string, object?>
                {
                    ["site"] = "wm-dpichanged-root",
                    ["win32_error"] = Marshal.GetLastWin32Error(),
                });
            }
        }

        _dpi = newDpi == 0 ? NativeMethods.GetDpiForWindow(_hwnd) : newDpi;
        _font?.Dispose();
        _font = GdiFont.TryCreate("Microsoft YaHei UI", DpiUnits.FontPixelHeight(_dpi));
        // R36：候选字号随 DPI 重建（先释放旧实例）；失败时 overlay 无可用字体，
        // 之后 ComputeOverlayFacts 会如实判 not-show，绝不拿旧字号的矩形硬摆。
        _ = CreateCandidateFont("wm-dpichanged");
        // DPI 变了行高也变：opt-in 下按新行高重定位 input/viewport child。
        RepositionInputSurfaceChildren();
        _store.InvalidateLayout("wm-dpichanged");
        BuildAndPublishLayout("wm-dpichanged");
        InvalidateInputSurface();
        // R34：DPI 变化后重建的字体行高变了——按新几何重锚 overlay。
        UpdateCandidateOverlayGeometry("wm-dpichanged");

        _trace("window", "dpi-changed", new Dictionary<string, object?>
        {
            ["dpi"] = _dpi,
            ["suggested_rect"] = lParam == IntPtr.Zero ? "(null)" : Marshal.PtrToStructure<TsfRect>(lParam).ToString(),
            ["thread_awareness"] = DpiNative.DescribeThreadAwareness(),
            ["font_row_height"] = _font?.RowHeight ?? 0,
        });
    }

    private void BuildAndPublishLayout(string reason)
    {
        // glyph/selection/underline/caret 坐标映射自真实输入 surface origin；默认 off 为 root 客户区。
        if (_font is null || !NativeMethods.GetClientRect(SurfaceHwnd, out var surfaceClient))
        {
            return;
        }

        var surfaceOrigin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(SurfaceHwnd, ref surfaceOrigin))
        {
            return;
        }

        // 显示边界 clip：opt-in 为真实 input∩全链裁剪祖先交集，默认 off 为 root 客户区。
        // clip ORIGIN 绝不可作 glyph origin；单元格仍从 surfaceOrigin 出发。
        TsfRect clientScreen;
        if (_childInputSurface)
        {
            var surface = DisplaySurface.FromChildIntersection(InputHwnd, ClipAncestorHwnd);
            if (!surface.Ok)
            {
                // 原生查询失败：如实保持布局无效（不清仓发布），绝不把上一帧 clip
                // 冒充成一次成功的更新。失效的 _layout 会让 GetScreenExt 如实返回失败/默认。
                _trace("window", "layout-clip-unavailable", new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["failure"] = surface.Failure,
                    ["hresult"] = surface.HResult,
                });
                return;
            }

            clientScreen = surface.Rect;
        }
        else
        {
            clientScreen = TsfRect.FromLtrb(surfaceOrigin.X, surfaceOrigin.Y,
                surfaceOrigin.X + surfaceClient.Width, surfaceOrigin.Y + surfaceClient.Height);
        }

        var text = _store.Document.Text;
        var advances = _font.MeasureAdvances(text);
        // opt-in 时 input child 已由 SetWindowPos 真实平移；本地排版不再二次减去 scroll。
        var scrollX = _childInputSurface ? 0 : _scrollX;
        var scrollY = _childInputSurface ? 0 : _scrollY;
        var snapshot = AcpLayoutSnapshot.Build(
            _store.LayoutGeneration,
            _store.Document.Version,
            text,
            advances,
            _font.RowHeight,
            clientScreen,
            surfaceOrigin.X + TextPaddingX,
            surfaceOrigin.Y + TextPaddingY,
            scrollX,
            scrollY);

        _lastLayout = snapshot;
        _clientScreen = clientScreen;
        _surfaceOriginScreen = surfaceOrigin;
        _store.PublishLayout(snapshot, notify: true);

        _trace("window", "layout-published", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["client"] = clientScreen.ToString(),
            ["row"] = snapshot.TextRowScreen.ToString(),
            ["scroll_x"] = _scrollX,
            ["scroll_y"] = _scrollY,
            ["document_version"] = snapshot.DocumentVersion,
            ["layout_generation"] = snapshot.LayoutGeneration,
            ["cells"] = snapshot.Cells.Count,
        });
    }

    /// <summary>
    /// 每个收到 WM_PAINT 的自有 hwnd 都 BeginPaint/EndPaint 校验自己的 update region。
    /// <paramref name="drawContent"/> 仅当该 hwnd 是真实输入 surface 时为 true：
    /// 默认 off → root；opt-in → input child。root/viewport（opt-in）只画白色背景，
    /// glyph/selection/underline/caret 全部只在真实输入 surface 上绘制。
    /// </summary>
    private void Paint(IntPtr hWnd, bool drawContent)
    {
        var ps = WindowNative.BeginPaint(hWnd, out var paint);
        try
        {
            var hdc = paint.Hdc;
            if (hdc == IntPtr.Zero)
            {
                return;
            }

            var background = Gdi.CreateSolidBrush(0x00FFFFFF);
            // 白底填当前 surface 自身客户区；每个 hwnd 用自己的 client rect，绝不用别的 surface 的尺寸。
            int fillW = _clientScreen.Width, fillH = _clientScreen.Height;
            if (NativeMethods.GetClientRect(hWnd, out var ownClient))
            {
                fillW = ownClient.Width;
                fillH = ownClient.Height;
            }

            var client = TsfRect.FromLtrb(0, 0, fillW, fillH);
            Gdi.FillRect(hdc, ref client, background);
            Gdi.DeleteObject(background);

            if (!drawContent || _font is null || _lastLayout is not { } layout)
            {
                return;
            }

            var previousFont = Gdi.SelectObject(hdc, _font.Handle);
            var previousColor = Gdi.SetTextColor(hdc, 0x00202020);
            Gdi.SetBkMode(hdc, Gdi.Transparent);

            var (selStart, selEnd) = _store.Document.Selection;
            if (selStart != selEnd)
            {
                DrawSelection(hdc, layout, selStart, selEnd);
            }

            var text = _store.Document.Text;
            // glyph 起点：surface 客户区本地坐标。opt-in 时 child 已真实平移，不再减 scroll。
            var x = TextPaddingX - (_childInputSurface ? 0 : _scrollX);
            var y = TextPaddingY - (_childInputSurface ? 0 : _scrollY);
            if (text.Length > 0)
            {
                Gdi.TextOutW(hdc, x, y, text, text.Length);
            }

            var (compStart, compEnd) = _store.CompositionSpan;
            if (_store.IsComposing && compEnd > compStart)
            {
                DrawCompositionUnderline(hdc, layout, compStart, compEnd);
            }

            if (selStart == selEnd)
            {
                DrawCaret(hdc, layout, selEnd);
            }

            Gdi.SetTextColor(hdc, previousColor);
            Gdi.SelectObject(hdc, previousFont);
        }
        finally
        {
            WindowNative.EndPaint(hWnd, ref paint);
        }
    }

    private void DrawCompositionUnderline(IntPtr hdc, AcpLayoutSnapshot layout, int start, int end)
    {
        if (start < 0 || end > layout.Cells.Count || end <= start)
        {
            return;
        }

        // glyph 坐标一律相对真实 surface origin（_surfaceOriginScreen），非 clip。
        var left = layout.Cells[start].Rect.Left - _surfaceOriginScreen.X;
        var right = layout.Cells[end - 1].Rect.Right - _surfaceOriginScreen.X;
        var y = layout.TextRowScreen.Bottom - _surfaceOriginScreen.Y;
        var rect = TsfRect.FromLtrb(left, y, right, y + 2);
        var brush = Gdi.CreateSolidBrush(0x00C06000);
        Gdi.FillRect(hdc, ref rect, brush);
        Gdi.DeleteObject(brush);
    }

    private void DrawSelection(IntPtr hdc, AcpLayoutSnapshot layout, int start, int end)
    {
        if (start < 0 || end > layout.Cells.Count || end <= start)
        {
            return;
        }

        var left = layout.Cells[start].Rect.Left - _surfaceOriginScreen.X;
        var right = layout.Cells[end - 1].Rect.Right - _surfaceOriginScreen.X;
        var top = layout.TextRowScreen.Top - _surfaceOriginScreen.Y;
        var bottom = layout.TextRowScreen.Bottom - _surfaceOriginScreen.Y;
        var rect = TsfRect.FromLtrb(left, top, right, bottom);
        var brush = Gdi.CreateSolidBrush(0x00E8D8C8);
        Gdi.FillRect(hdc, ref rect, brush);
        Gdi.DeleteObject(brush);
    }

    private void DrawCaret(IntPtr hdc, AcpLayoutSnapshot layout, int acp)
    {
        var x = layout.BoundaryX(acp) - _surfaceOriginScreen.X;
        var top = layout.TextRowScreen.Top - _surfaceOriginScreen.Y;
        var bottom = layout.TextRowScreen.Bottom - _surfaceOriginScreen.Y;
        var rect = TsfRect.FromLtrb(x, top, x + CaretWidth, bottom);
        var brush = Gdi.CreateSolidBrush(0x00000000);
        Gdi.FillRect(hdc, ref rect, brush);
        Gdi.DeleteObject(brush);
    }

    // -------------------------------------------------------------------
    // R34：自有候选 overlay（root 客户区内 WS_EX_NOACTIVATE child）。
    // 只绘制当前 presenter 快照；绝不夺焦、绝不写任何 TSF Show、绝不模拟恢复输入。
    // -------------------------------------------------------------------

    /// <summary>
    /// 按当前真实几何重算 overlay 矩形并 ShowWindow/HideWindow。
    /// <paramref name="snapshot"/> 为 null 时只按 live 快照重定位（resize/DPI/clip 恢复）。
    /// 快照无效 → 如实清空并隐藏；input 全裁剪 → 只隐藏展示（live 快照仍随最新发布更新）。
    /// 所有操作都在 UI 线程执行；不创造“成功可见”的假结果。
    /// </summary>
    private void ApplyCandidateOverlayOnUiThread(CandidateSnapshot? snapshot,
        bool hideByClip, string reason)
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            return;
        }

        var target = _overlay.Apply(snapshot, hideByClip);
        if (target is null)
        {
            SetOverlayWindowVisible(false);
            _trace("window", snapshot is { Valid: false } ? "overlay-clear-invalid"
                    : hideByClip ? "overlay-hide-clipped" : "overlay-hidden",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["element_id"] = snapshot?.ElementId,
                    ["generation"] = snapshot?.Generation,
                    ["failure"] = snapshot?.Failure,
                });
            return;
        }

        var facts = ComputeOverlayFacts(target);
        if (!facts.WantsShow)
        {
            SetOverlayWindowVisible(false);
            _trace("window", "overlay-suppressed", new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["element_id"] = target.ElementId,
                ["input_visible"] = facts.InputVisible.IsEmpty ? "(none)" : facts.InputVisible.ToString(),
                ["client"] = facts.RootClientScreen.ToString(),
                ["row"] = facts.RowScreen.ToString(),
                ["failure"] = facts.Failure,
            });
            return;
        }

        PositionOverlayWindow(facts.Rect);
        _trace("window", "overlay-positioned", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["element_id"] = target.ElementId,
            ["generation"] = target.Generation,
            ["rect"] = facts.Rect.ToString(),
            ["client"] = facts.RootClientScreen.ToString(),
            ["row"] = facts.RowScreen.ToString(),
            ["entries"] = target.Entries.Count,
            ["input_visible"] = facts.InputVisible.ToString(),
        });
    }

    /// <summary>
    /// 计算 overlay 的<b>目标矩形</b>与是否应展示。R36：尺寸全部来自候选字体
    /// （<see cref="_candidateFont"/>）的<b>实测</b>行高与文本宽度，边界是 root 的
    /// <b>真实客户区</b>（GetClientRect + ClientToScreen，不含标题栏），摆放交给
    /// <see cref="CandidateOverlayLayout.Place"/>——真实窗口与测试共用同一实现。
    /// 放不下时如实返回 WantsShow=false + Failure，绝不夹到客户区顶部覆盖组合行。
    /// </summary>
    private OverlayFacts ComputeOverlayFacts(CandidateSnapshot snapshot)
    {
        var hwndOk = _overlayHwnd != IntPtr.Zero;
        var rowOk = TryGetTextRowScreenRect(out var row);
        var inputOk = TryGetVisibleInputSurfaceRect(out var inputVisible, out _);
        var clientOk = TryGetRootClientScreenRect(out var clientScreen);

        if (!hwndOk || !rowOk || !inputOk || !clientOk)
        {
            return new OverlayFacts(hwndOk,
                clientOk ? clientScreen : default,
                inputOk ? inputVisible : default,
                rowOk ? row : default, default, false,
                !clientOk ? "client-rect-query-failed"
                    : !rowOk ? "text-row-query-failed"
                    : !inputOk ? "input-visible-query-failed" : "overlay-hwnd-missing");
        }

        // 候选字体缺失（创建失败/DPI 重建失败）：没有真实度量就不摆，如实失败。
        if (_candidateFont is not { } font)
        {
            return new OverlayFacts(true, clientScreen, inputVisible, row, default, false,
                "candidate-font-missing");
        }

        var entryCount = Math.Max(1, Math.Min(snapshot.Entries.Count,
            HostDrawnCandidatePresenter.MaxPageEntries));

        // 用同一字体实测最宽"序号 + 候选"与页脚；任一测量失败即如实失败。
        var contentWidth = 0;
        for (var i = 0; i < snapshot.Entries.Count && i < entryCount; i++)
        {
            var label = CandidateOverlayLayout.EntryLabel(i, snapshot.Entries[i].Text);
            if (!font.TryMeasureText(label, out var measured))
            {
                return new OverlayFacts(true, clientScreen, inputVisible, row, default, false,
                    $"measure-entry-{i}-failed");
            }

            contentWidth = Math.Max(contentWidth, measured);
        }

        var footer = CandidateOverlayLayout.FooterText(snapshot.CurrentPage);
        if (!font.TryMeasureText(footer, out var footerWidth))
        {
            return new OverlayFacts(true, clientScreen, inputVisible, row, default, false,
                "measure-footer-failed");
        }

        contentWidth = Math.Max(contentWidth, footerWidth);

        // 页脚高度 = 一整行同字体高度（字号已含 ascent/descent，不额外估算）。
        var placement = CandidateOverlayLayout.Place(clientScreen, row, entryCount,
            font.RowHeight, contentWidth, font.RowHeight);
        if (!placement.Show)
        {
            return new OverlayFacts(true, clientScreen, inputVisible, row, default, false,
                placement.Failure);
        }

        return new OverlayFacts(true, clientScreen, inputVisible, row, placement.Rect, true);
    }

    /// <summary>移动 overlay 到目标矩形并显示（NOACTIVATE|NOZORDER）。</summary>
    private void PositionOverlayWindow(TsfRect screenRect)
    {
        if (_overlayHwnd == IntPtr.Zero || !NativeMethods.GetClientRect(_hwnd, out var rootClient))
        {
            return;
        }

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(_hwnd, ref origin))
        {
            return;
        }

        var x = screenRect.Left - origin.X;
        var y = screenRect.Top - origin.Y;
        var w = Math.Max(1, screenRect.Width);
        var h = Math.Max(1, screenRect.Height);
        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_SHOWWINDOW;
        _ = WindowNative.SetWindowPos(_overlayHwnd, IntPtr.Zero, x, y, w, h, flags);
        _ = WindowNative.InvalidateRect(_overlayHwnd, IntPtr.Zero, false);
    }

    /// <summary>显隐 overlay（NOACTIVATE|NOMOVE|NOSIZE|NOZORDER；读回真实状态）。</summary>
    private void SetOverlayWindowVisible(bool visible)
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            return;
        }

        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE
            | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
            | (visible ? NativeMethods.SWP_SHOWWINDOW : NativeMethods.SWP_HIDEWINDOW);
        _ = WindowNative.SetWindowPos(_overlayHwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
        if (visible)
        {
            _ = WindowNative.InvalidateRect(_overlayHwnd, IntPtr.Zero, false);
        }
    }

    /// <summary>重定位（已有快照时按当前几何重锚；无快照为空操作）。
    /// input 交集恢复非空时不再按裁剪隐藏标记直接返回——如实走恢复路径，
    /// 用 presenter 的<b>当前</b>同代快照重锚展示。</summary>
    private void UpdateCandidateOverlayGeometry(string reason)
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            return;
        }

        // R34D/E：真实 input 交集为空（scroll/移动把 input 推出 viewport）——
        // 只隐藏展示；live 快照保留并仍随裁剪中的 Update 刷新。
        var clipped = false;
        if (_childInputSurface)
        {
            var surface = DisplaySurface.FromChildIntersection(InputHwnd, ClipAncestorHwnd);
            clipped = !surface.Ok || surface.Rect.IsEmpty;
        }

        if (clipped)
        {
            ApplyCandidateOverlayOnUiThread(null, hideByClip: true, reason);
            return;
        }

        if (_overlay.HiddenByClip)
        {
            // 曾裁剪隐藏、现几何恢复可见：走真实恢复路径——只展示 presenter 当前
            // 同元素同代数快照；无当前/代数失配 → 清空并隐藏，绝不复活旧内容。
            RestoreCandidateOverlayAfterClip(reason);
            return;
        }

        ApplyCandidateOverlayOnUiThread(null, hideByClip: false, reason);
    }

    /// <summary>对外：应用一次 presenter 发布（UI 线程派发内调用）。
    /// 几何全裁剪时如实保持隐藏（同快照仍更新，展示不外翻）。</summary>
    internal void ApplyCandidateSnapshot(CandidateSnapshot snapshot, string reason)
    {
        // 裁剪中/几何已全裁剪：更新 live 快照但绝不恢复展示——恢复只认当前代数。
        var clipped = _overlay.HiddenByClip;
        if (!clipped && _childInputSurface && snapshot.Valid)
        {
            var surface = DisplaySurface.FromChildIntersection(InputHwnd, ClipAncestorHwnd);
            clipped = !surface.Ok || surface.Rect.IsEmpty;
        }

        ApplyCandidateOverlayOnUiThread(snapshot, hideByClip: clipped, reason);
    }

    /// <summary>对外：隐藏并清空展示（End/焦点丢失；UI 线程派发内调用）。
    /// 清空<b>全部</b>展示状态——live 快照、clip 标记、元素 id/代数一并归零，
    /// 之后任何几何重算/恢复都不得复活展示。</summary>
    internal void HideCandidateOverlay(string reason)
    {
        _overlay.Clear();
        SetOverlayWindowVisible(false);
        _trace("window", "overlay-hide", new Dictionary<string, object?>
        {
            ["reason"] = reason,
        });
    }

    /// <summary>对外：input 全裁剪——只隐藏展示（同快照+同代数保留）。</summary>
    internal void HideCandidateOverlayForClip(string reason)
        => ApplyCandidateOverlayOnUiThread(null, hideByClip: true, reason);

    /// <summary>对外：裁剪恢复——只把 presenter 的<b>当前</b>同元素同代数有效快照
    /// 重新显示（绝不重读 COM；已 End/被替换/发布失败的来源绝不复活旧内容）。</summary>
    internal void RestoreCandidateOverlayAfterClip(string reason)
    {
        // 恢复数据只认 presenter 当前发布槽：元素已 End/被替换时 presenter.Current
        // 为 null 或换代数——如实清空并隐藏，而不是把旧的 live 帧重新摆回去。
        var restored = _overlay.Restore(_session?.UiElements?.HostDrawnPresenter?.Current);
        ApplyCandidateOverlayOnUiThread(restored, hideByClip: false, reason);
    }

    /// <summary>对外：真实几何/可见性检查（在 UI 线程读取原生事实）。
    /// IsWindowVisible 为 true 但 GetWindowRect 失败 → 如实判 Unavailable
    /// （“读几何失败”绝不冒充“已隐藏/空矩形”）；快照内容只报 live 一份。</summary>
    internal OverlayInspection InspectCandidateOverlay(string stage)
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            return OverlayInspection.Unavailable("overlay-hwnd-missing");
        }

        var live = _overlay.Live;
        var visibleNow = NativeMethods.IsWindowVisible(_overlayHwnd);
        var rect = default(TsfRect);
        if (visibleNow)
        {
            if (!NativeMethods.GetWindowRect(_overlayHwnd, out var nativeRect))
            {
                return OverlayInspection.Unavailable(
                    $"get-window-rect-failed win32={Marshal.GetLastWin32Error()}");
            }

            rect = TsfRect.FromLtrb(nativeRect.Left, nativeRect.Top,
                nativeRect.Right, nativeRect.Bottom);
        }

        return new OverlayInspection(true, null,
            visibleNow ? rect : default, live?.ElementId ?? 0, live?.Generation ?? 0,
            live?.ElementId ?? 0, live?.Entries.Count ?? 0, _overlay.HiddenByClip,
            visibleNow);
    }

    /// <summary>
    /// R34D：把真实 overlay 窗口包装成 <see cref="ICandidateOverlay"/> 适配器——
    /// presenter 的 Apply/Hide/Invalidate 控制流与测试替身同一接口，全部操作仍
    /// 在 owning UI 线程执行；未建窗口时适配器如实空转（presenter 侧已判失败）。
    /// </summary>
    internal ICandidateOverlay CreateCandidateOverlayChannel()
        => new HostCandidateOverlayChannel(this);

    private sealed class HostCandidateOverlayChannel : ICandidateOverlay
    {
        private readonly TsfHostWindow _window;

        internal HostCandidateOverlayChannel(TsfHostWindow window) => _window = window;

        public void Apply(CandidateSnapshot snapshot)
            => _window.ApplyCandidateSnapshot(snapshot, "presenter-apply");

        public void Hide(uint id, uint generation, string reason)
            => _window.HideCandidateOverlay($"presenter-hide:{reason}");

        public void Invalidate()
        {
            if (_window._overlayHwnd != IntPtr.Zero)
            {
                _ = WindowNative.InvalidateRect(_window._overlayHwnd, IntPtr.Zero, false);
            }
        }
    }

    /// <summary>
    /// overlay 自身 WM_PAINT：在自身 update region 内画快照（无快照→只画背景）。
    /// R36：使用<b>候选独立字体</b>（与 ComputeOverlayFacts 测量矩形的是同一个
    /// <see cref="_candidateFont"/> 实例），行高/页脚/序号标签与测量完全一致——
    /// 绝不出现"按一种字体算高度、用另一种字体画文字"的错位。
    /// </summary>
    private void PaintCandidateOverlay(IntPtr hWnd)
    {
        var paint = default(PaintStruct);
        var hdc = WindowNative.BeginPaint(hWnd, out paint);
        try
        {
            var background = Gdi.CreateSolidBrush(0x00F4F0EA);
            var client = TsfRect.FromLtrb(0, 0,
                paint.Paint.Right - paint.Paint.Left + 8, paint.Paint.Bottom - paint.Paint.Top + 8);
            if (NativeMethods.GetClientRect(hWnd, out var ownClient))
            {
                client = TsfRect.FromLtrb(0, 0, ownClient.Width, ownClient.Height);
            }

            Gdi.FillRect(hdc, ref client, background);
            Gdi.DeleteObject(background);

            if (_overlay.Live is not { } snapshot || !snapshot.Valid || _overlay.HiddenByClip)
            {
                return;
            }

            if (_candidateFont is not { } font)
            {
                return;
            }

            var previousFont = Gdi.SelectObject(hdc, font.Handle);
            var normalColor = Gdi.SetTextColor(hdc, 0x00202020);
            Gdi.SetBkMode(hdc, Gdi.Transparent);

            var rowH = font.RowHeight;
            var x = CandidateOverlayLayout.OuterPadding;
            var y = CandidateOverlayLayout.OuterPadding;
            for (var i = 0; i < snapshot.Entries.Count && i < HostDrawnCandidatePresenter.MaxPageEntries; i++)
            {
                var entry = snapshot.Entries[i];
                var selected = i == snapshot.SelectionOnPage;
                if (selected)
                {
                    var selRect = TsfRect.FromLtrb(0, y, client.Width, y + rowH);
                    var selBrush = Gdi.CreateSolidBrush(0x00D8E8FF);
                    Gdi.FillRect(hdc, ref selRect, selBrush);
                    Gdi.DeleteObject(selBrush);
                }

                // 页内序号 1..N（用户看到的当前页顺序）；真实候选序号只在数据/诊断里。
                var text = CandidateOverlayLayout.EntryLabel(i, entry.Text);
                Gdi.TextOutW(hdc, x, y, text, text.Length);
                y += rowH;
            }

            // 页脚：同字体一整行，1 基页码（用户可见文案，不用调试缩写）。
            var footer = CandidateOverlayLayout.FooterText(snapshot.CurrentPage);
            Gdi.SetTextColor(hdc, 0x00606060);
            Gdi.TextOutW(hdc, x, y, footer, footer.Length);
            Gdi.SetTextColor(hdc, normalColor);
            Gdi.SelectObject(hdc, previousFont);
        }
        finally
        {
            WindowNative.EndPaint(hWnd, ref paint);
        }
    }

    public void Dispose()
    {
        // 失败初始化/提前返回也必须销毁窗口并注销类，不能只释放字体。
        if (_hwnd != IntPtr.Zero)
        {
            WindowNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_secondHwnd != IntPtr.Zero)
        {
            WindowNative.DestroyWindow(_secondHwnd);
            _secondHwnd = IntPtr.Zero;
        }

        // opt-in child 随 root 销毁；此处仅归零字段，避免悬空句柄被误用。
        // live 候选快照与代数也一并清空——销毁后任何路径都不能复活展示。
        _inputHwnd = IntPtr.Zero;
        _viewportHwnd = IntPtr.Zero;
        _overlayHwnd = IntPtr.Zero;
        _overlay.Clear();
        _inputFocusInitialized = false;

        if (_classRegistered)
        {
            WindowNative.UnregisterClassW(ClassName, _instance);
            _classRegistered = false;
        }

        _font?.Dispose();
        _font = null;
        _candidateFont?.Dispose();
        _candidateFont = null;
        _created = false;
    }

    private sealed class Invocation
    {
        internal Invocation(Action action) => Action = action;

        internal Action Action { get; }

        internal ManualResetEventSlim Done { get; } = new(false);

        internal Exception? Error { get; set; }
    }
}
