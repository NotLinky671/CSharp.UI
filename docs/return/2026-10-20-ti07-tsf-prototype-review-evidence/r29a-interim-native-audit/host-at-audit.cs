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

    /// <summary>SWP_NOSIZE|SWP_NOACTIVATE 用于真实 child 移动时的标志组合。</summary>
    internal const uint SwpChildMove = 0x0001 | 0x0002 | 0x0004 | 0x0010; // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE

    /// <summary>移动 child 窗口而不改尺寸/Z 序/激活；失败由调用方如实记录，不合成假坐标。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
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
    private IntPtr _viewportHwnd;
    private IntPtr _inputHwnd;
    private bool _inputFocusInitialized;
    private string? _inputFocusResult;

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

    /// <summary>绘制/键盘/CHAR/焦点归属的真实 surface：opt-in 为 input，默认 off 为 root。</summary>
    private IntPtr SurfaceHwnd => _childInputSurface && _inputHwnd != IntPtr.Zero ? _inputHwnd : _hwnd;

    /// <summary>显示边界祖先：opt-in 为 viewport child，默认 off 为 root 自身。</summary>
    private IntPtr ClipAncestorHwnd => _childInputSurface && _viewportHwnd != IntPtr.Zero ? _viewportHwnd : _hwnd;

    /// <summary>R29A 初始聚焦前置条件（托管可测）：root 前台、input 与 root 同进程同 UI 线程才允许。</summary>
    internal static bool CanInitializeInputFocus(IntPtr foregroundHwnd, IntPtr rootHwnd,
        IntPtr inputHwnd, uint rootThreadId, uint rootProcessId, uint inputThreadId, uint inputProcessId)
        => foregroundHwnd != IntPtr.Zero
            && foregroundHwnd == rootHwnd
            && inputHwnd != IntPtr.Zero
            && rootThreadId != 0
            && inputThreadId == rootThreadId
            && inputProcessId == rootProcessId;

    /// <summary>本实例调用 ShowWindow 的次数：隐藏宿主回归据此证明“绝不显示窗口”。</summary>
    internal int ShowWindowCalls { get; private set; }

    internal bool Create(int width, int height, string title, bool show = true,
        bool childInputSurface = false)
    {
        _uiThreadId = NativeMethods.CurrentWin32ThreadId();
        _childInputSurface = childInputSurface;
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
        WindowNative.SetWindowPos(_viewportHwnd, IntPtr.Zero, 0, 0,
            rootClient.Width, rootClient.Height, flags);
        WindowNative.SetWindowPos(_inputHwnd, IntPtr.Zero, TextPaddingX, -_scrollY,
            width, inputHeight, flags);

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
    /// R29A 一次性初始聚焦：仅当 root 已在前台且 input 与 root 同进程同 UI 线程时，
    /// 才从 UI 线程 SetFocus(input)。否则如实拒绝，绝不 AttachThreadInput/TOPMOST/IME-HWND 操作。
    /// 绝不从 scroll/layout/paint/geometry 回调触发。
    /// </summary>
    internal bool InitializeInputFocus()
    {
        if (!_childInputSurface || _inputHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_inputFocusInitialized)
        {
            return NativeMethods.GetFocus() == _inputHwnd;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var inputThread = NativeMethods.GetWindowThreadProcessId(_inputHwnd, out var inputPid);
        var rootThread = NativeMethods.GetWindowThreadProcessId(_hwnd, out var rootPid);

        var allowed = CanInitializeInputFocus(foreground, _hwnd, _inputHwnd,
            rootThread, rootPid, inputThread, inputPid);
        if (!allowed)
        {
            _inputFocusResult = "refused:foreground-or-thread";
            _trace("window", "input-focus-refused", new Dictionary<string, object?>
            {
                ["foreground"] = $"0x{foreground.ToInt64():X}",
                ["root"] = $"0x{_hwnd.ToInt64():X}",
                ["input"] = $"0x{_inputHwnd.ToInt64():X}",
                ["root_thread"] = rootThread,
                ["input_thread"] = inputThread,
            });
            return false;
        }

        _inputFocusInitialized = true;
        Invoke(() =>
        {
            WindowNative.SetFocus(_inputHwnd);
            var ok = NativeMethods.GetFocus() == _inputHwnd;
            _inputFocusResult = ok ? "ok" : "set-focus-mismatch";
            _trace("window", "input-focus-initialized", new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{_inputHwnd.ToInt64():X}",
                ["set_focus"] = ok,
                ["focus_now"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
            });
        });
        return _inputFocusResult == "ok";
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
            WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
        });
    }

    internal void ScrollBy(int deltaY) => Invoke(() =>
    {
        _scrollY += deltaY;
        MoveInputSurfaceForScroll();
        _store.InvalidateLayout("scroll-y");
        BuildAndPublishLayout("scroll-y");
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
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
            WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
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
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
    });

    internal void SetScroll(int x, int y) => Invoke(() =>
    {
        _scrollX = x;
        _scrollY = y;
        MoveInputSurfaceForScroll();
        _store.InvalidateLayout("scroll-set");
        BuildAndPublishLayout("scroll-set");
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
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

        var flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        var moved = WindowNative.SetWindowPos(_inputHwnd, IntPtr.Zero,
            TextPaddingX - _scrollX, -_scrollY, 0, 0, flags);
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

    internal void MoveWindowBy(int dx, int dy) => Invoke(() =>
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, rect.Left + dx, rect.Top + dy, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _store.InvalidateLayout("window-move");
        BuildAndPublishLayout("window-move");
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
    });

    internal void ResizeWindow(int width, int height) => Invoke(() =>
    {
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _store.InvalidateLayout("window-resize");
        BuildAndPublishLayout("window-resize");
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
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

    internal TsfRect ClientScreenRect
    {
        get
        {
            var result = default(TsfRect);
            Invoke(() =>
            {
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

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 第二个自有窗口只用于制造真实 WM_KILLFOCUS；它的消息全部交给默认过程，
        // 不触碰主窗口的会话、布局或退出消息。
        if (hWnd == _secondHwnd)
        {
            return WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        var isRoot = hWnd == _hwnd;
        var isInput = _childInputSurface && hWnd == _inputHwnd;
        var isViewport = _childInputSurface && hWnd == _viewportHwnd;

        // opt-in 时 input child 走完整消息路径（绘制/键盘/CHAR/焦点）；viewport 只重排。
        // 其他未知 child 一律 DefWindowProc。
        if (!isRoot && !isInput && !isViewport)
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
                        WindowNative.InvalidateRect(hWnd, IntPtr.Zero, false);
                    }

                    return IntPtr.Zero;

                case WindowNative.WM_SETFOCUS:
                    // 只有真实输入 surface 获得焦点才上报；root 绝不冒充 input。
                    if (isInput)
                    {
                        _session?.OnWindowFocus(true);
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_KILLFOCUS:
                    if (isInput)
                    {
                        _session?.OnWindowFocus(false);
                    }
                    return IntPtr.Zero;

                case WindowNative.WM_KEYDOWN:
                case WindowNative.WM_SYSKEYDOWN:
                    if ((isInput || isRoot) && _session?.RouteKeyDown(wParam, lParam) == true)
                    {
                        return IntPtr.Zero;
                    }

                    break;

                case WindowNative.WM_KEYUP:
                case WindowNative.WM_SYSKEYUP:
                    if ((isInput || isRoot) && _session?.RouteKeyUp(wParam, lParam) == true)
                    {
                        return IntPtr.Zero;
                    }

                    break;

                case WindowNative.WM_CHAR:
                    if ((isInput || isRoot) && _session?.HandleChar(wParam) == true)
                    {
                        return IntPtr.Zero;
                    }

                    break;

                case WindowNative.WM_MOUSEWHEEL:
                    if (isInput || isRoot)
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
                        _store.InvalidateLayout(msg == WindowNative.WM_SIZE ? "wm-size" : "wm-move");
                        BuildAndPublishLayout(msg == WindowNative.WM_SIZE ? "wm-size" : "wm-move");
                    }

                    break;

                case WindowNative.WM_PAINT:
                    if (isInput || isRoot)
                    {
                        Paint(hWnd);
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
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void HandleDpiChanged(IntPtr wParam, IntPtr lParam)
    {
        var newDpi = (uint)((long)wParam & 0xFFFF);
        if (lParam != IntPtr.Zero)
        {
            // 使用系统建议 rect（PMv2 下为真实物理像素），不自行乘系数。
            var suggested = Marshal.PtrToStructure<TsfRect>(lParam);
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
                suggested.Left, suggested.Top, suggested.Width, suggested.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        _dpi = newDpi == 0 ? NativeMethods.GetDpiForWindow(_hwnd) : newDpi;
        _font?.Dispose();
        _font = GdiFont.TryCreate("Microsoft YaHei UI", DpiUnits.FontPixelHeight(_dpi));
        _store.InvalidateLayout("wm-dpichanged");
        BuildAndPublishLayout("wm-dpichanged");
        WindowNative.InvalidateRect(_hwnd, IntPtr.Zero, false);

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

        // 显示边界 clip：opt-in 为真实 input∩viewport 交集，默认 off 为 root 客户区。
        // clip ORIGIN 绝不可作 glyph origin；单元格仍从 surfaceOrigin 出发。
        TsfRect clientScreen;
        if (_childInputSurface)
        {
            var surface = DisplaySurface.FromChildIntersection(InputHwnd, ClipAncestorHwnd);
            if (!surface.Ok)
            {
                // 原生查询失败：如实保持上一帧 clip，绝不合成新坐标假装成功。
                clientScreen = _clientScreen;
            }
            else
            {
                clientScreen = surface.Rect;
            }
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

    private void Paint(IntPtr hWnd)
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
            // 白底填当前 surface 自身客户区（opt-in 为 input child，默认 off 为 root）。
            var fillW = _clientScreen.Width;
            var fillH = _clientScreen.Height;
            if (_childInputSurface && hWnd == _inputHwnd
                && NativeMethods.GetClientRect(hWnd, out var ownClient))
            {
                fillW = ownClient.Width;
                fillH = ownClient.Height;
            }

            var client = TsfRect.FromLtrb(0, 0, fillW, fillH);
            Gdi.FillRect(hdc, ref client, background);
            Gdi.DeleteObject(background);

            if (_font is null || _lastLayout is not { } layout)
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
        _inputHwnd = IntPtr.Zero;
        _viewportHwnd = IntPtr.Zero;
        _inputFocusInitialized = false;

        if (_classRegistered)
        {
            WindowNative.UnregisterClassW(ClassName, _instance);
            _classRegistered = false;
        }

        _font?.Dispose();
        _font = null;
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
