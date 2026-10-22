using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK;

/// <summary>
/// TI-07 原生声明：自有 HWND 子类化与 IMM32 宽接口所需的最小入口集。
/// </summary>
/// <remarks>
/// 只包含 Windows 桌面路径实际调用的 user32/imm32/glfw3 入口；声明本身跨平台可编译，
/// 实际调用全部位于 <c>OperatingSystem.IsWindows()</c> 路径。声明与 TI-01 探针核对过的二进制一致。
/// </remarks>
internal static class Win32Ime
{
    internal const int GWLP_WNDPROC = -4;

    internal const uint WM_ACTIVATE = 0x0006;
    internal const uint WM_KILLFOCUS = 0x0008;
    internal const uint WM_NCDESTROY = 0x0082;
    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const uint WM_SHOWWINDOW = 0x0018;
    internal const uint SC_MINIMIZE = 0xF020;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_SYSCHAR = 0x0106;
    internal const uint WM_UNICHAR = 0x0109;
    internal const uint WM_IME_STARTCOMPOSITION = 0x010D;
    internal const uint WM_IME_ENDCOMPOSITION = 0x010E;
    internal const uint WM_IME_COMPOSITION = 0x010F;
    internal const uint WM_IME_SETCONTEXT = 0x0281;
    internal const uint WM_IME_NOTIFY = 0x0282;
    internal const uint WM_IME_CHAR = 0x0286;
    internal const uint WM_IME_REQUEST = 0x0288;

    /// <summary>WM_KEYDOWN 的 wParam：输入法已消费该按键。</summary>
    internal const int VK_PROCESSKEY = 0xE5;

    /// <summary>WM_UNICHAR 探测值：发送方用它询问应用是否支持 UTF-32 字符消息。</summary>
    internal const int UNICODE_NOCHAR = 0xFFFF;

    // WM_IME_SETCONTEXT 的 lParam 位（已按本机 SDK um/imm.h 第 460-464 行核对）：
    // 高位 0x80000000 控制系统组合窗（本框架自绘预编辑，需要屏蔽）；bit0 是候选窗，绝不能误清。
    internal const long ISC_SHOWUICANDIDATEWINDOW = 0x00000001;
    internal const long ISC_SHOWUIGUIDELINE = 0x40000000;
    internal const long ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000;
    internal const long ISC_SHOWUIALLCANDIDATEWINDOW = 0x0000000F;
    internal const long ISC_SHOWUIALL = unchecked((long)0xC000000F);

    // IMM32 组合串索引/标志（已按 um/imm.h 第 517-528 行核对；全部以位标志理解）。
    internal const uint GCS_COMPREADSTR = 0x0001;
    internal const uint GCS_COMPREADATTR = 0x0002;
    internal const uint GCS_COMPREADCLAUSE = 0x0004;
    internal const uint GCS_COMPSTR = 0x0008;
    internal const uint GCS_COMPATTR = 0x0010;
    internal const uint GCS_COMPCLAUSE = 0x0020;
    internal const uint GCS_CURSORPOS = 0x0080;
    internal const uint GCS_DELTASTART = 0x0100;
    internal const uint GCS_RESULTREADSTR = 0x0200;
    internal const uint GCS_RESULTREADCLAUSE = 0x0400;
    internal const uint GCS_RESULTSTR = 0x0800;
    internal const uint GCS_RESULTCLAUSE = 0x1000;

    /// <summary>预编辑侧标志并集（读侧 + 当前串/属性/条款/光标/差异起点）。</summary>
    internal const long GcsPreeditMask =
        GCS_COMPREADSTR | GCS_COMPREADATTR | GCS_COMPREADCLAUSE
        | GCS_COMPSTR | GCS_COMPATTR | GCS_COMPCLAUSE | GCS_CURSORPOS | GCS_DELTASTART;

    /// <summary>结果侧标志并集。</summary>
    internal const long GcsResultMask =
        GCS_RESULTREADSTR | GCS_RESULTREADCLAUSE | GCS_RESULTSTR | GCS_RESULTCLAUSE;

    /// <summary>
    /// 全部 GCS_* 标志位（0x1FBF）。WM_IME_COMPOSITION 的“平台取消”判定必须基于该并集：
    /// 仅带 RESULTCLAUSE 等结果元数据的消息不是取消（R6）。
    /// </summary>
    internal const long GcsAnyMask = GcsPreeditMask | GcsResultMask;

    // 输入法模式位（imm.h IME_CMODE_*）：自有 HIMC 上的临时模式切换——只改本窗口上下文，
    // 不动用户全局输入模式（验收工具在自有窗口上验证中英文路径）。
    internal const uint IME_CMODE_ALPHANUMERIC = 0x0000;
    internal const uint IME_CMODE_NATIVE = 0x0001;   // 中文/日文/韩文原生转换（中文模式的语义位）
    internal const uint IME_CMODE_LANGUAGE = 0x0003;
    internal const uint IME_CMODE_FULLSHAPE = 0x0008;
    internal const uint IME_CMODE_ROMAN = 0x0010;
    internal const uint IME_CMODE_NOCONVERSION = 0x0100;
    internal const uint IME_CMODE_FIXED = 0x0800;
    internal const uint IME_SMODE_NONE = 0x0000;

    // 候选窗/组合窗定位
    internal const uint CFS_RECT = 0x0001;
    internal const uint CFS_POINT = 0x0002;
    internal const uint CFS_CANDIDATEPOS = 0x0040;
    internal const uint CFS_EXCLUDE = 0x0080;

    // ImmAssociateContextEx
    internal const uint IACE_DEFAULT = 0x0010;

    // ImmNotifyIME
    internal const uint NI_COMPOSITIONSTR = 0x0015;
    internal const uint CPS_CANCEL = 0x0004;

    // WM_IME_REQUEST 子命令
    internal const uint IMR_COMPOSITIONWINDOW = 0x0001;
    internal const uint IMR_CANDIDATEWINDOW = 0x0002;
    internal const uint IMR_QUERYCHARPOSITION = 0x0006;

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

    /// <summary>
    /// IMECHARPOSITION（imm.h，已按本机 SDK um/imm.h 核对：36 字节）。
    /// <see cref="dwSize"/> 与 <see cref="dwCharPos"/> 由调用方填写（缓冲区大小 + 组合串 UTF-16 偏移）；
    /// 自绘组合串的应用负责填 <see cref="pt"/>（该字符的屏幕坐标）、<see cref="cLineHeight"/>（行高像素）
    /// 与 <see cref="rcDocument"/>（可编辑区域的屏幕矩形），屏幕坐标允许为负。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IMECHARPOSITION
    {
        public uint dwSize;
        public uint dwCharPos;
        public POINT pt;
        public uint cLineHeight;
        public RECT rcDocument;
    }

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>客户区像素 → 屏幕坐标；多显示器下返回值允许为负。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    // --- IMM32 宽接口 ---

    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmGetContext(IntPtr hWnd);

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
    internal static extern bool ImmAssociateContextEx(IntPtr hWnd, IntPtr hIMC, uint dwFlags);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmNotifyIME(IntPtr hIMC, uint dwAction, uint dwIndex, uint dwValue);

    // 自有 HIMC 的开启/转换模式读写（验收工具做中文/英文路径切换与恢复；只作用于本窗口上下文）。
    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmGetOpenStatus(IntPtr hIMC);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fopen);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmSetConversionStatus(IntPtr hIMC, uint fdwConversion, uint fdwSentence);

    /// <summary>GLFW 原生入口：与 OpenTK 4.9.4 加载的是同一个 glfw3.dll。</summary>
    [DllImport("glfw3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "glfwGetWin32Window")]
    internal static extern IntPtr GlfwGetWin32Window(IntPtr window);

    /// <summary>SetWindowLongPtr(GWLP_WNDPROC)。本程序集只在 64 位 Windows 桌面路径使用。</summary>
    internal static IntPtr SetWindowProc(IntPtr hWnd, IntPtr newProc)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, GWLP_WNDPROC, newProc)
            : throw new PlatformNotSupportedException("IME 桥只在 64 位进程下支持。");

    /// <summary>GetWindowLongPtr(GWLP_WNDPROC)。</summary>
    internal static IntPtr GetWindowProc(IntPtr hWnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWLP_WNDPROC)
            : throw new PlatformNotSupportedException("IME 桥只在 64 位进程下支持。");

    /// <summary>客户区像素 → 屏幕像素；窗口无效或转换失败返回 false，不作坐标猜测。</summary>
    internal static bool TryClientToScreen(IntPtr hWnd, int clientX, int clientY, out int screenX, out int screenY)
    {
        var point = new POINT { X = clientX, Y = clientY };
        if (hWnd == IntPtr.Zero || !ClientToScreen(hWnd, ref point))
        {
            screenX = 0;
            screenY = 0;
            return false;
        }

        screenX = point.X;
        screenY = point.Y;
        return true;
    }
}
