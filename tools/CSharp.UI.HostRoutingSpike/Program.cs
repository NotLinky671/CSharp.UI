using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;

// TI-07 P3a 原生按键路由探针（返修版）。
//
// 本工具只“观察与驱动”产品行为：按键拦截使用**产品** TsfMessageHook（改写 MSG 的正式实现），
// 消息分发使用**产品** OpenTkHost 的 GLFW.PollEvents。
//
// 上一版假通过的根因（本版修正）：
//   * WH_GETMESSAGE 的**返回值不消费消息**；必须在真实消费时改写 lParam 指向的 MSG。
//   * WM_CHAR 的 wParam 是 Unicode 字符，不是虚拟键：C 键产生 U+0063，而 VK 是 0x43。
//
// 明确不做：可见 UI、强制前台、全局键盘注入、输入法/语言配置修改。
// 合成 PostMessage 只证明原生顺序与消费效果，不是微软拼音验收。
internal static class Program
{
    private static readonly List<string> Log = [];

    // 只记录本工具自己合成的字符码点（不采集任何用户文本）。
    private static readonly List<int> SyntheticChars = [];

    private static int _failures;
    private static IntPtr _ownHwnd;
    private static TsfMessageHook? _productHook;
    private static bool _negativeControl;

    [STAThread]
    private static int Main(string[] args)
    {
        _negativeControl = args.Contains("--negative-control", StringComparer.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("[spike] SKIP non-windows");
            return 0;
        }

        Log.Add($"[spike] apartment={Thread.CurrentThread.GetApartmentState()} "
            + $"osThreadId={GetCurrentThreadId()}");

        try
        {
            var options = new UiApplicationOptions
            {
                Title = "CSharp.UI P3a routing spike (hidden)",
                Width = 240,
                Height = 120,
                VSync = false,
                StartVisible = false,
                Started = context =>
                {
                    try
                    {
                        RunScenarios(context);
                    }
                    catch (Exception ex)
                    {
                        Log.Add($"[spike] EXCEPTION {ex.GetType().Name}: {ex.Message}");
                        _failures++;
                        context.RequestExit(1);
                    }
                },
            };

            var exit = UiApplication.Run(new Rectangle(Color.Parse("#2563EB")), options);
            _failures += exit == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Add($"[spike] HOST EXCEPTION {ex.GetType().Name}: {ex.Message}");
            _failures++;
        }

        foreach (var line in Log)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine(_failures == 0 ? "[spike] RESULT PASS" : $"[spike] RESULT FAIL count={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunScenarios(UiHostContext context)
    {
        var host = UiApplication.CurrentHost;
        if (host is null)
        {
            Log.Add("[spike] FAIL no host");
            _failures++;
            context.RequestExit(1);
            return;
        }

        _ownHwnd = WindowsImeBridge.GetWindowHandle(host.Window);
        Log.Add($"[spike] own hwnd=0x{_ownHwnd:X} osThreadId={GetCurrentThreadId()}");
        if (_ownHwnd == IntPtr.Zero)
        {
            Log.Add("[spike] FAIL no own hwnd");
            _failures++;
            context.RequestExit(1);
            return;
        }

        // 证据采集钩子：只记录本工具合成的 WM_CHAR 码点（在真实消费改写之后）。
        InstallObserver();

        // **产品**钩子：判定回调由探针提供（确定性合成策略），钩子本体与 MSG 改写是产品实现。
        _productHook = new TsfMessageHook(
            _ownHwnd,
            GetCurrentThreadId(),
            (hwnd, message, wParam, lParam) => wParam == 0x43);

        if (_negativeControl)
        {
            // 负向对照：安装产品钩子但**禁用 MSG 改写**，C 必然产生回声。
            // 这精确证明“无回声”来自改写，而不是返回值或计数巧合。
            _productHook.SuppressRewriteForTesting = true;
            Log.Add("[spike] NEGATIVE CONTROL: hook installed but MSG rewrite disabled; C echo expected");
        }

        if (!_productHook.Install())
        {
            Log.Add($"[spike] FAIL product hook install err={_productHook.InstallFailureCode}");
            _failures++;
            context.RequestExit(1);
            return;
        }
        else
        {
            Log.Add("[spike] product TsfMessageHook installed");
        }

        // 合成按键（明确标注）：A 未消费、C 应被消费。
        PostKey(_ownHwnd, 0x0100, 0x41, isUp: false); // A down
        PostKey(_ownHwnd, 0x0101, 0x41, isUp: true); // A up
        // TEMP: C disabled to isolate A duplicate
        PostKey(_ownHwnd, 0x0100, 0x43, isUp: false); // C down
        PostKey(_ownHwnd, 0x0101, 0x43, isUp: true); // C up

        // 交给**产品 GLFW pump** 分发，随后核对（不在工具里自建消息循环）。
        context.Dispatcher.Post(() => Verify(context));
    }

    private static void Verify(UiHostContext context)
    {
        var aChars = SyntheticChars.Count(c => c == 0x61);
        var cChars = SyntheticChars.Count(c => c == 0x63);

        Log.Add($"[spike] synthetic chars: A(U+0061)={aChars} C(U+0063)={cChars} total={SyntheticChars.Count}");
        Log.Add($"[spike] hook {_productHook}");

        if (_negativeControl)
        {
            // 负向对照必须失败：没有 MSG 改写时 C 一定产生回声。
            if (cChars == 0)
            {
                Log.Add("[spike] NEGATIVE-CONTROL PROBLEM: C produced no char without rewrite");
                _failures++;
            }
            else
            {
                Log.Add("[spike] negative control correct: C echoed without MSG rewrite");
            }

            Finish(context);
            return;
        }

        if (aChars < 1)
        {
            Log.Add("[spike] FAIL unconsumed A produced no character");
            _failures++;
        }

        if (cChars != 0)
        {
            Log.Add($"[spike] FAIL consumed C still produced {cChars} character(s)");
            _failures++;
        }

        if (_productHook is { } h)
        {
            if (h.ConsumedCount == 0)
            {
                Log.Add("[spike] FAIL hook never classified C as consumed");
                _failures++;
            }

            if (h.RewriteCount != h.ConsumedCount)
            {
                Log.Add($"[spike] FAIL consumed={h.ConsumedCount} != rewritten={h.RewriteCount}");
                _failures++;
            }
        }

        Finish(context);
    }

    private static void Finish(UiHostContext context)
    {
        _productHook?.Dispose();
        Log.Add("[spike] product hook torn down");

        if (_observerHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_observerHook);
            _observerHook = IntPtr.Zero;
        }

        context.RequestExit(_failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// 投递一条合成的按键消息。
    /// </summary>
    /// <remarks>
    /// <b>lParam 必须区分 keydown/keyup：</b>keyup 要置位 30（previous key state）与 31（transition state），
    /// 否则 <c>TranslateMessage</c> 会把它当成 keydown 并再生成一个 <c>WM_CHAR</c>——
    /// 这正是上一版“普通字符出现两次”的来源（工具缺陷，不是产品缺陷）。
    /// </remarks>
    private static void PostKey(IntPtr hwnd, int msg, int vk, bool isUp)
    {
        var scan = MapVirtualKeyW((uint)vk, 0) & 0xFF;
        var bits = 1L | ((long)scan << 16);
        if (isUp)
        {
            bits |= 1L << 30; // previous key state: down
            bits |= 1L << 31; // transition state: released
        }

        PostMessageW(hwnd, (uint)msg, (IntPtr)vk, (IntPtr)bits);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>证据采集：只记录本工具合成的 WM_CHAR 码点。</summary>
    private static void InstallObserver()
    {
        _observerProc = (nCode, wParam, lParam) =>
        {
            if (nCode >= 0)
            {
                var msg = Marshal.PtrToStructure<NativeMsg>(lParam);
                if (msg.Hwnd == _ownHwnd && wParam == (IntPtr)TsfMessageHookPolicy.PmRemove)
                {
                    Log.Add($"[seq] msg=0x{msg.Message:X4} wParam=0x{(int)msg.WParam:X4}");
                    if (msg.Message == TsfMessageHookPolicy.WmChar)
                    {
                        SyntheticChars.Add((int)msg.WParam);
                        Log.Add($"[audit-synthetic-char] U+{(int)msg.WParam:X4}");
                    }
                }
            }

            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        };

        _observerHook = SetWindowsHookExW(3, _observerProc, IntPtr.Zero, GetCurrentThreadId());
    }

    private static IntPtr _observerHook;
    private static HookProc? _observerProc;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
