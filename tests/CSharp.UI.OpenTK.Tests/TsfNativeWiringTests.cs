using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P2b 原生接线测试：真实 TSF <c>CreateContext</c> + <c>Push</c> + sink advice，
/// 使用<b>隐藏的自有窗口</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>只证明协议接线：</b>验证真实 msctf 接受本 store 作为 <c>ITextStoreACP</c>（CCW QI 成功）、
/// 上下文可创建并压栈、sink 被 advice。不发送键盘、不改全局输入法配置、不显示窗口。
/// </para>
/// <para>
/// <b>套间：</b>TSF 要求 STA。本测试在自建 STA 线程上运行，不改进程/主线程套间策略。
/// </para>
/// <para>非 Windows 或 TSF 不可用时测试直接返回（不算跳过，见报告的平台边界说明）。</para>
/// </remarks>
public sealed class TsfNativeWiringTests
{
    private const int WS_OVERLAPPED = 0x00000000;
    private const int SW_HIDE = 0;

    [Fact]
    public void 真实TSF接受生产store作为ITextStoreACP并建立上下文()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var failures = new List<string>();
        var thread = new Thread(() => RunWiringProbe(failures));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "原生接线探测超时。");
        Console.WriteLine($"[P2b] wiring probe completed, failures={failures.Count}");
        Assert.True(failures.Count == 0, "原生接线失败：" + string.Join("; ", failures));
    }

    private static void RunWiringProbe(List<string> failures)
    {
        var className = $"CSharp.UI.P2b.Wiring.{Environment.ProcessId}";
        var instance = GetModuleHandleW(null);

        WndProcDelegate proc = (hWnd, msg, wParam, lParam) => DefWindowProcW(hWnd, msg, wParam, lParam);
        var procPtr = Marshal.GetFunctionPointerForDelegate(proc);

        var wc = new WndClassW
        {
            Style = 0,
            WndProc = procPtr,
            Instance = instance,
            ClassName = className,
        };

        var atom = RegisterClassW(ref wc);
        if (atom == 0)
        {
            failures.Add($"RegisterClassW 失败，Win32={Marshal.GetLastWin32Error()}");
            return;
        }

        var hwnd = IntPtr.Zero;
        AcpTextStore? store = null;
        ITfThreadMgr? threadMgr = null;
        ITfDocumentMgr? documentMgr = null;
        ITfContext? context = null;
        try
        {
            // 隐藏窗口（不 ShowWindow）：只作为 HWND 与显示表面查询对象。
            hwnd = CreateWindowExW(0, className, "CSharp.UI P2b wiring (hidden)", WS_OVERLAPPED,
                0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                failures.Add($"CreateWindowExW 失败，Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            // 明确保持隐藏（不调用 ShowWindow）。
            _ = SW_HIDE;

            store = new AcpTextStore();
            store.ResetAuthoritativeText("abcd");
            store.AttachWindow(hwnd);

            // R6（审核）：从<b>无 sink</b> 的 store 开始，不预先安装测试 sink。
            // 这样后续关于“原生连接是否建立”的断言不可能被测试自己的 sink 污染。
            if (store.HasSink)
            {
                failures.Add("测试前置错误：store 在 CreateContext 之前已有 sink。");
                return;
            }

            Console.WriteLine("[P2b] store has no sink before CreateContext");

            // 真实 TSF：创建线程管理器（当前 STA 线程）。
            Console.WriteLine("[P2b] hidden window + store ready (no sink)");
            var createHr = TsfNative.TF_CreateThreadMgr(out threadMgr);
            Console.WriteLine($"[P2b] TF_CreateThreadMgr hr=0x{createHr:X8}");
            if (createHr < 0 || threadMgr is null)
            {
                failures.Add($"TF_CreateThreadMgr 失败 {TsfHResult.Describe(createHr)}");
                return;
            }

            var activateHr = threadMgr.Activate(out var clientId);
            if (activateHr < 0)
            {
                failures.Add($"ITfThreadMgr::Activate 失败 {TsfHResult.Describe(activateHr)}");
                return;
            }

            Console.WriteLine($"[P2b] Activate hr=0x{activateHr:X8} clientId={clientId}");
            var dmHr = threadMgr.CreateDocumentMgr(out documentMgr);
            if (dmHr < 0 || documentMgr is null)
            {
                failures.Add($"CreateDocumentMgr 失败 {TsfHResult.Describe(dmHr)}");
                return;
            }

            // 关键一步：msctf 对 store 做 QI(ITextStoreACP) 并建立上下文。
            var createContextHr = documentMgr.CreateContext(
                clientId, 0, store, out context, out var editCookie);
            if (createContextHr < 0 || context is null)
            {
                failures.Add($"CreateContext 失败 {TsfHResult.Describe(createContextHr)}"
                    + $"（msctf 未能把 store 当作 ITextStoreACP 接受）");
                return;
            }

            if (editCookie == 0)
            {
                failures.Add("CreateContext 返回的 edit cookie 为 0。");
                return;
            }

            Console.WriteLine($"[P2b] CreateContext hr=0x{createContextHr:X8} cookie={editCookie} storeHasSink={store.HasSink}");
            // 压栈：上下文成为文档管理器的顶层。
            var pushHr = documentMgr.Push(context);
            if (pushHr < 0)
            {
                failures.Add($"Push 失败 {TsfHResult.Describe(pushHr)}");
                return;
            }

            // 记录<b>实际</b>观察到的 advice 状态（不预设结论）。
            Console.WriteLine($"[P2b] after CreateContext+Push: storeHasSink={store.HasSink} mask=0x{store.SinkMask:X}");

            // R6（审核）：store 在 CreateContext 之前没有任何 sink，因此此刻出现的 sink 只能由
            // msctf 自己通过 ITextStoreACP::AdviseSink 安装——这是原生连接的真实证据。
            if (!store.HasSink)
            {
                failures.Add("CreateContext+Push 之后 msctf 没有 advise 本 store 的 sink。");
            }
            else if (store.SinkMask == 0)
            {
                failures.Add("msctf advise 的 sink mask 为 0（未订阅任何事件）。");
            }

            // 锁协议在真实会话上仍可用（同步读锁授予）。
            var lockHr = store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out var sessionHr);
            if (lockHr != TsfHResult.S_OK || sessionHr < 0)
            {
                failures.Add($"真实会话上 RequestLock 失败 {TsfHResult.Describe(sessionHr)}");
            }

            Console.WriteLine($"[P2b] Push hr=0x{pushHr:X8} lock hr=0x{lockHr:X8} session=0x{sessionHr:X8}");
            // 上下文确实可查询（证明 push 生效）。
            var topHr = documentMgr.GetTop(out var top);
            if (topHr < 0 || top is null)
            {
                failures.Add($"GetTop 失败 {TsfHResult.Describe(topHr)}");
            }

            if (top is not null)
            {
                ReleaseCom(top);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 只释放自己创建/拥有的引用；不对借用对象 FinalReleaseComObject。
            if (documentMgr is not null)
            {
                try
                {
                    documentMgr.Pop(0);
                }
                catch (Exception)
                {
                }

                ReleaseCom(documentMgr);
            }

            if (context is not null)
            {
                ReleaseCom(context);
            }

            if (threadMgr is not null)
            {
                try
                {
                    threadMgr.Deactivate();
                }
                catch (Exception)
                {
                }

                ReleaseCom(threadMgr);
            }

            store?.Dispose();

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
            }

            UnregisterClassW(className, instance);
        }
    }

    /// <summary>
    /// 释放自己拥有的 COM 引用；非 Windows 上不可能有 COM 对象，直接忽略。
    /// </summary>
    /// <remarks>只用于本测试自建/自拥有的 RCW，绝不对借用或共享对象调用 FinalReleaseComObject。</remarks>
    private static void ReleaseCom(object? value)
    {
        if (value is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (Exception)
        {
            // 释放失败不影响测试结论（进程即将结束该线程的套间）。
        }
    }

    private sealed class NoOpSink : ITextStoreACPSink
    {
        public int OnTextChange(uint dwFlags, ref TsTextChange pChange) => 0;

        public int OnSelectionChange() => 0;

        public int OnLayoutChange(int lcode, uint vcView) => 0;

        public int OnStatusChange(uint dwFlags) => 0;

        public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs) => 0;

        public int OnLockGranted(uint dwLockFlags) => 0;

        public int OnStartEditTransaction() => 0;

        public int OnEndEditTransaction() => 0;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern ushort RegisterClassW(ref WndClassW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
