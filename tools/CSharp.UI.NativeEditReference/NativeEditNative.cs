using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>屏幕矩形（与 ImeProbe 的 POINT/RECT 互转）。</summary>
internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;

    internal int Height => Bottom - Top;

    internal bool IsEmpty => Right <= Left || Bottom <= Top;

    internal static NativeRect FromClient(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    internal NativeRect Intersect(NativeRect other) => new(
        Math.Max(Left, other.Left),
        Math.Max(Top, other.Top),
        Math.Min(Right, other.Right),
        Math.Min(Bottom, other.Bottom));

    internal bool Contains(NativeRect other)
        => other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>
/// 原生 EDIT 对照工具用到的 Win32 声明。全部逐项与本机 user32/imm32/comctl32 文档一致；
/// 字符串入口统一使用 `...W`，结构体按平台宽度显式标注。
/// </summary>
internal static class NativeEditNative
{
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CLIPCHILDREN = 0x02000000;
    internal const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    internal const int WS_POPUP = unchecked((int)0x80000000);

    internal const int ES_MULTILINE = 0x0004;
    internal const int ES_AUTOVSCROLL = 0x0040;
    internal const int ES_NOHIDESEL = 0x0100;
    internal const int WS_VSCROLL = 0x00200000;

    internal const uint EM_SETSEL = 0x00B1;
    internal const uint EM_REPLACESEL = 0x00C2;
    internal const uint EM_GETSEL = 0x00B0;
    internal const uint EM_SCROLLCARET = 0x00B7;
    internal const uint EM_LINEFROMCHAR = 0x00C9;
    internal const uint EM_POSFROMCHAR = 0x00D6;
    internal const uint EM_GETFIRSTVISIBLELINE = 0x00CE;
    internal const uint EM_LINESCROLL = 0x00B6;
    internal const uint EM_GETLINECOUNT = 0x00BA;
    internal const uint EM_LINEINDEX = 0x00BB;
    internal const int WM_GETTEXT = 0x000D;
    internal const int WM_GETTEXTLENGTH = 0x000E;
    internal const int WM_SETTEXT = 0x000C;
    internal const int WM_IME_STARTCOMPOSITION = 0x010D;
    internal const int WM_IME_ENDCOMPOSITION = 0x010E;
    internal const int WM_IME_COMPOSITION = 0x010F;
    internal const int WM_IME_SETCONTEXT = 0x0281;
    internal const int WM_IME_NOTIFY = 0x0282;
    internal const int WM_IME_CHAR = 0x0286;
    internal const int WM_COMMAND = 0x0111;

    // EDIT 通知码（winuser.h）
    internal const int EN_SETFOCUS = 0x0100;
    internal const int EN_KILLFOCUS = 0x0200;
    internal const int EN_CHANGE = 0x0300;

    internal const uint GCS_COMPSTR = 0x0008;
    internal const uint GCS_COMPATTR = 0x0010;
    internal const uint GCS_CURSORPOS = 0x0080;
    internal const uint GCS_RESULTSTR = 0x0800;
    internal const uint SCS_SETSTR = 0x0009;
    internal const uint SCS_GETSTR = 0x0008;
    internal const uint SCS_SETCONVERSIONWINDOW = 0x0089;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOOWNERZORDER = 0x0200;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const uint SW_SHOW = 5;
    internal const uint SW_HIDE = 0;

    internal const int GWL_STYLE = -16;
    internal const int GWLP_USERDATA = -21;

    internal const uint PM_REMOVE = 0x0001;
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_APP_INVOKE = 0x8000 + 0x0100;

    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_SPACE = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        internal uint cbSize;
        internal uint style;
        internal IntPtr lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        internal IntPtr lpszMenuName;
        internal IntPtr lpszClassName;
        internal IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal IntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal int ptX;
        internal int ptY;
    }

    internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal delegate IntPtr SubclassProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr idSubclass, IntPtr refData);

    // ---- comctl32 子类化（观测后必须调用 DefSubclassProc） ----

    [DllImport("comctl32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, IntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint dwExStyle, IntPtr lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    /// <summary>类名以字符串传入（系统预定义类，例如 "EDIT"/"STATIC"）。</summary>
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClassW(IntPtr lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr GetFocus();

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    internal const uint GW_OWNER = 4;

    // ---- IMM32：成对使用 ImmGetContext / ImmReleaseContext ----

    [DllImport("imm32.dll", ExactSpelling = true)]
    internal static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int ImmGetCompositionStringW(IntPtr hIMC, uint dwIndex, IntPtr lpBuf,
        int dwBufLen);
}

/// <summary>一次性标量查询结果：区分“查询成功”与“查询失败”，不把失败当成 0。</summary>
internal readonly record struct NativeQuery<T>(int HResult, T Value, string? Failure)
{
    internal bool Ok => HResult >= 0;
}
