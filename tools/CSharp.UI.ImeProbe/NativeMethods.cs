using System.Runtime.InteropServices;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// TI-01 探针使用的原生声明。只包含探针自己需要的 Win32/IMM32 入口，不修改生产框架。
/// </summary>
/// <remarks>
/// 目标依赖：Windows 10/11 自带的 user32.dll / imm32.dll / gdi32.dll / msctf.dll（系统组件）。
/// 该文件保留跨平台编译能力（只是 <c>DllImport</c> 声明），实际调用只在 Windows 上进行。
/// </remarks>
internal static class NativeMethods
{
    internal const int GWLP_WNDPROC = -4;
    internal const int GWLP_USERDATA = -21;

    // 窗口消息
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_MOVE = 0x0003;
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_ACTIVATE = 0x0006;
    internal const uint WM_SETFOCUS = 0x0007;
    internal const uint WM_KILLFOCUS = 0x0008;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_ACTIVATEAPP = 0x001C;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    internal const uint WM_INPUTLANGCHANGE = 0x0051;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_NCDESTROY = 0x0082;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_DEADCHAR = 0x0103;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_SYSCHAR = 0x0106;
    internal const uint WM_UNICHAR = 0x0109;
    internal const uint WM_IME_STARTCOMPOSITION = 0x010D;
    internal const uint WM_IME_ENDCOMPOSITION = 0x010E;
    internal const uint WM_IME_COMPOSITION = 0x010F;
    internal const uint WM_IME_SETCONTEXT = 0x0281;
    internal const uint WM_IME_NOTIFY = 0x0282;
    internal const uint WM_IME_CONTROL = 0x0283;
    internal const uint WM_IME_COMPOSITIONFULL = 0x0284;
    internal const uint WM_IME_SELECT = 0x0285;
    internal const uint WM_IME_CHAR = 0x0286;
    internal const uint WM_IME_REQUEST = 0x0288;

    // WM_IME_CONTROL 命令（imm.h）
    internal const uint IMC_SETCANDIDATEPOS = 0x0008;

    // IMM32 组合串索引
    internal const uint GCS_COMPREADSTR = 0x0001;
    internal const uint GCS_COMPSTR = 0x0008;
    internal const uint GCS_COMPATTR = 0x0010;
    internal const uint GCS_COMPCLAUSE = 0x0020;
    internal const uint GCS_CURSORPOS = 0x0080;
    internal const uint GCS_RESULTREADSTR = 0x0200;
    internal const uint GCS_RESULTSTR = 0x0800;
    internal const uint GCS_RESULTCLAUSE = 0x1000;

    // WM_IME_NOTIFY 命令
    internal const uint IMN_CLOSESTATUSWINDOW = 0x0001;
    internal const uint IMN_OPENSTATUSWINDOW = 0x0002;
    internal const uint IMN_CHANGECANDIDATE = 0x0003;
    internal const uint IMN_CLOSECANDIDATE = 0x0004;
    internal const uint IMN_OPENCANDIDATE = 0x0005;
    internal const uint IMN_SETCONVERSIONMODE = 0x0006;
    internal const uint IMN_SETSENTENCEMODE = 0x0007;
    internal const uint IMN_SETOPENSTATUS = 0x0008;
    internal const uint IMN_SETCANDIDATEPOS = 0x0009;
    internal const uint IMN_SETCOMPOSITIONFONT = 0x000A;
    internal const uint IMN_SETCOMPOSITIONWINDOW = 0x000B;
    internal const uint IMN_SETSTATUSWINDOWPOS = 0x000C;
    internal const uint IMN_GUIDELINE = 0x000D;
    internal const uint IMN_PRIVATE = 0x000E;

    // 候选窗/组合窗定位
    internal const uint CFS_DEFAULT = 0x0000;
    internal const uint CFS_RECT = 0x0001;
    internal const uint CFS_POINT = 0x0002;
    internal const uint CFS_FORCE_POSITION = 0x0020;
    internal const uint CFS_CANDIDATEPOS = 0x0040;
    internal const uint CFS_EXCLUDE = 0x0080;

    // ImmAssociateContextEx
    internal const uint IACE_DEFAULT = 0x0010;

    // 转换模式（中文）
    internal const uint IME_CMODE_ALPHANUMERIC = 0x0000;
    internal const uint IME_CMODE_NATIVE = 0x0001;
    internal const uint IME_CMODE_LANGUAGE = 0x0003;
    internal const uint IME_CMODE_FULLSHAPE = 0x0008;
    internal const uint IME_CMODE_ROMAN = 0x0010;
    internal const uint IME_CMODE_SOFTKBD = 0x0080;
    internal const uint IME_CMODE_NOCONVERSION = 0x0100;

    // ImmNotifyIME
    internal const uint NI_OPENCANDIDATE = 0x0010;
    internal const uint NI_CLOSECANDIDATE = 0x0011;
    internal const uint NI_COMPOSITIONSTR = 0x0015;
    internal const uint CPS_COMPLETE = 0x0001;
    internal const uint CPS_CONVERT = 0x0002;
    internal const uint CPS_REVERT = 0x0003;
    internal const uint CPS_CANCEL = 0x0004;

    // 键盘/焦点
    internal const uint SM_CXSCREEN = 0;
    internal const uint SM_CYSCREEN = 1;
    internal const uint SM_XVIRTUALSCREEN = 76;
    internal const uint SM_YVIRTUALSCREEN = 77;
    internal const uint SM_CXVIRTUALSCREEN = 78;
    internal const uint SM_CYVIRTUALSCREEN = 79;

    internal const int SW_SHOW = 5;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;

    internal const uint SRCCOPY = 0x00CC0020;
    internal const uint DIB_RGB_COLORS = 0;
    internal const int BI_RGB = 0;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;
    internal const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CANDIDATEFORM
    {
        public uint dwIndex;
        public uint dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COMPOSITIONFORM
    {
        public uint dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CANDIDATELIST_HEADER
    {
        public uint dwSize;
        public uint dwStyle;
        public uint dwCount;
        public uint dwSelection;
        public uint dwPageStart;
        public uint dwPageSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(uint nIndex);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>只读异步键盘状态（高位=当前按下）；用于人工确认键检测，不注入任何输入。</summary>
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // --- 线程 caret（诊断用途：隐藏 caret 定位对照，绝不 ShowCaret） ---

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>GUI_CARETBLINKING：GUITHREADINFO.flags 位，caret 正在闪烁（可见）。</summary>
    internal const uint GUI_CARETBLINKING = 0x00000001;

    /// <summary>获取指定线程的 GUI 信息（含 hwndCaret）；idThread=0 表示调用线程。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateCaret(IntPtr hWnd, IntPtr hBitmap, int nWidth, int nHeight);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCaretPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCaretPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyCaret();

    /// <summary>调用线程消息队列的键盘焦点窗口（不等于前台窗口）。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetFocus();

    // NotifyWinEvent 常量（本机 SDK winuser.h 10.0.26100.0 核对）：
    // EVENT_OBJECT_LOCATIONCHANGE=0x800B；OBJID_CARET=(LONG)0xFFFFFFF8；CHILDID_SELF=0。
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    internal const int OBJID_CARET = unchecked((int)0xFFFFFFF8);
    internal const int CHILDID_SELF = 0;

    /// <summary>向无障碍/IME 兼容层报告对象事件；诊断仅用于报告自有 caret 的真实位置变化。</summary>
    [DllImport("user32.dll")]
    internal static extern void NotifyWinEvent(uint event_, IntPtr hwnd, int idObject, int idChild);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// 当前原生（Win32）线程 ID。非 Windows 上返回 0，且绝不加载/调用 kernel32，
    /// 这样日志的单元测试在 Linux/macOS 也只是纯托管 CPU 测试。
    /// </summary>
    internal static uint CurrentWin32ThreadId() => OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>MAPVK_VK_TO_VSC：把虚拟键码映射为扫描码（用于真实扫描码 SendInput 对照）。</summary>
    internal const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int CoGetApartmentType(out int apartmentType, out int apartmentQualifier);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, byte[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal const uint SMTO_NORMAL = 0x0000;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
        IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>该探针只在 Windows 上有意义。</summary>
    internal static bool IsWindowsHost() => OperatingSystem.IsWindows();

    // --- IMM32 ---

    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmCreateContext();

    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmDestroyContext(IntPtr hIMC);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ImmGetCompositionStringW(IntPtr hIMC, uint dwIndex, byte[]? lpBuf, int dwBufLen);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CANDIDATEFORM lpCandidate);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmGetCandidateWindow(IntPtr hIMC, uint dwIndex, out CANDIDATEFORM lpCandidate);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompositionForm);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmGetCompositionWindow(IntPtr hIMC, out COMPOSITIONFORM lpCompositionForm);

    [DllImport("imm32.dll")]
    internal static extern uint ImmGetCandidateListW(IntPtr hIMC, uint dwIndex, byte[]? lpCandList, uint dwBufLen);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmAssociateContextEx(IntPtr hWnd, IntPtr hIMC, uint dwFlags);

    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmIsUIMessage(IntPtr hwndIME, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ImmGetDescriptionW(IntPtr hIMC, [Out] char[] lpszDescription, uint uBufLen);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ImmGetIMEFileNameW(IntPtr hIMC, [Out] char[] lpszFileName, uint uBufLen);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmSetConversionStatus(IntPtr hIMC, uint fdwConversion, uint fdwSentence);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmGetOpenStatus(IntPtr hIMC);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmSetOpenStatus(IntPtr hIMC, [MarshalAs(UnmanagedType.Bool)] bool fOpen);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmNotifyIME(IntPtr hIMC, uint dwAction, uint dwIndex, uint dwValue);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmIsIME(IntPtr hkl);

    // --- GDI（证据截图）---

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    // --- GLFW 原生入口（与 OpenTK 4.9.4 使用同一个 glfw3.dll）---

    [DllImport("glfw3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "glfwGetWin32Window")]
    internal static extern IntPtr GlfwGetWin32Window(IntPtr window);

    [DllImport("glfw3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "glfwGetVersionString")]
    private static extern IntPtr GlfwGetVersionString();

    internal static string GetGlfwVersionString()
    {
        try
        {
            var ptr = GlfwGetVersionString();
            return ptr == IntPtr.Zero ? "(null)" : Marshal.PtrToStringAnsi(ptr) ?? "(null)";
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>SetWindowLong(GWLP_WNDPROC)，按进程位数选择入口。</summary>
    internal static IntPtr SetWindowProc(IntPtr hWnd, IntPtr newProc)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, GWLP_WNDPROC, newProc)
            : new IntPtr(SetWindowLong32(hWnd, GWLP_WNDPROC, newProc.ToInt32()));

    /// <summary>GetWindowLong(GWLP_WNDPROC)，按进程位数选择入口。</summary>
    internal static IntPtr GetWindowProc(IntPtr hWnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWLP_WNDPROC)
            : new IntPtr(GetWindowLong32(hWnd, GWLP_WNDPROC));

    internal static string GetWindowClassName(IntPtr hWnd)
    {
        var buffer = new char[256];
        var length = GetClassNameW(hWnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    /// <summary>把 Win32 消息号格式化为可读名称，未知消息返回十六进制。</summary>
    internal static string MessageName(uint msg) => msg switch
    {
        WM_DESTROY => "WM_DESTROY",
        WM_MOVE => "WM_MOVE",
        WM_SIZE => "WM_SIZE",
        WM_ACTIVATE => "WM_ACTIVATE",
        WM_SETFOCUS => "WM_SETFOCUS",
        WM_KILLFOCUS => "WM_KILLFOCUS",
        WM_CLOSE => "WM_CLOSE",
        WM_ACTIVATEAPP => "WM_ACTIVATEAPP",
        WM_SETTINGCHANGE => "WM_SETTINGCHANGE",
        WM_INPUTLANGCHANGEREQUEST => "WM_INPUTLANGCHANGEREQUEST",
        WM_INPUTLANGCHANGE => "WM_INPUTLANGCHANGE",
        WM_DISPLAYCHANGE => "WM_DISPLAYCHANGE",
        WM_NCDESTROY => "WM_NCDESTROY",
        WM_KEYDOWN => "WM_KEYDOWN",
        WM_KEYUP => "WM_KEYUP",
        WM_CHAR => "WM_CHAR",
        WM_DEADCHAR => "WM_DEADCHAR",
        WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
        WM_SYSKEYUP => "WM_SYSKEYUP",
        WM_SYSCHAR => "WM_SYSCHAR",
        WM_UNICHAR => "WM_UNICHAR",
        WM_IME_STARTCOMPOSITION => "WM_IME_STARTCOMPOSITION",
        WM_IME_ENDCOMPOSITION => "WM_IME_ENDCOMPOSITION",
        WM_IME_COMPOSITION => "WM_IME_COMPOSITION",
        WM_IME_SETCONTEXT => "WM_IME_SETCONTEXT",
        WM_IME_NOTIFY => "WM_IME_NOTIFY",
        WM_IME_CONTROL => "WM_IME_CONTROL",
        WM_IME_COMPOSITIONFULL => "WM_IME_COMPOSITIONFULL",
        WM_IME_SELECT => "WM_IME_SELECT",
        WM_IME_CHAR => "WM_IME_CHAR",
        WM_IME_REQUEST => "WM_IME_REQUEST",
        _ => $"0x{msg:X4}",
    };

    internal static string ImnName(IntPtr wParam) => (ulong)wParam.ToInt64() switch
    {
        IMN_CLOSESTATUSWINDOW => "IMN_CLOSESTATUSWINDOW",
        IMN_OPENSTATUSWINDOW => "IMN_OPENSTATUSWINDOW",
        IMN_CHANGECANDIDATE => "IMN_CHANGECANDIDATE",
        IMN_CLOSECANDIDATE => "IMN_CLOSECANDIDATE",
        IMN_OPENCANDIDATE => "IMN_OPENCANDIDATE",
        IMN_SETCONVERSIONMODE => "IMN_SETCONVERSIONMODE",
        IMN_SETSENTENCEMODE => "IMN_SETSENTENCEMODE",
        IMN_SETOPENSTATUS => "IMN_SETOPENSTATUS",
        IMN_SETCANDIDATEPOS => "IMN_SETCANDIDATEPOS",
        IMN_SETCOMPOSITIONFONT => "IMN_SETCOMPOSITIONFONT",
        IMN_SETCOMPOSITIONWINDOW => "IMN_SETCOMPOSITIONWINDOW",
        IMN_SETSTATUSWINDOWPOS => "IMN_SETSTATUSWINDOWPOS",
        IMN_GUIDELINE => "IMN_GUIDELINE",
        IMN_PRIVATE => "IMN_PRIVATE",
        _ => $"IMN_0x{(ulong)wParam.ToInt64():X4}",
    };

    /// <summary>把 WM_IME_COMPOSITION 的 lParam 标志位展开成名字列表。</summary>
    internal static List<string> GcsFlags(IntPtr lParam)
    {
        var value = (ulong)lParam.ToInt64();
        var flags = new List<string>();
        if ((value & GCS_COMPREADSTR) != 0) flags.Add("GCS_COMPREADSTR");
        if ((value & GCS_COMPSTR) != 0) flags.Add("GCS_COMPSTR");
        if ((value & GCS_COMPATTR) != 0) flags.Add("GCS_COMPATTR");
        if ((value & GCS_COMPCLAUSE) != 0) flags.Add("GCS_COMPCLAUSE");
        if ((value & GCS_CURSORPOS) != 0) flags.Add("GCS_CURSORPOS");
        if ((value & GCS_RESULTREADSTR) != 0) flags.Add("GCS_RESULTREADSTR");
        if ((value & GCS_RESULTSTR) != 0) flags.Add("GCS_RESULTSTR");
        if ((value & GCS_RESULTCLAUSE) != 0) flags.Add("GCS_RESULTCLAUSE");
        return flags;
    }
}
