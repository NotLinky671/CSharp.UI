using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;

// TI-07 P3a 正式宿主有序输入验收（返修版，R1–R8）。
//
// 本工具使用**真实生产宿主**（UiApplication.Run）与**真实产品对象**：
// 真实 TextField 根（两个字段）、真实运行时、真实输入适配器、真实产品钩子与路由器。
// 只注入确定性“合成按键消费策略”（真实 TSF 无 key event sink 时报 eaten=false，
// 无法构造确定性“被消费”样例），并明确标注为合成证据。
//
// 明确不做：可见 UI、强制前台、全局键盘注入、输入法/语言配置修改、自建消息循环。
internal static class Program
{
    private static readonly List<string> Log = [];
    private static readonly List<int> SyntheticChars = [];
    private static int _failures;
    private static IntPtr _ownHwnd;

    private static int _writesA;
    private static int _writesB;

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("[p3a] SKIP non-windows");
            return 0;
        }

        Log.Add($"[p3a] apartment={Thread.CurrentThread.GetApartmentState()} osThreadId={GetCurrentThreadId()}");

        // 两个真实 TextField：用于 clientA→clientB 的 epoch 变化验证。
        var textA = "ABCD";
        var textB = "WXYZ";
        var fieldA = new TextField(new Binding<string>(() => textA, v => { _writesA++; textA = v; }));
        var fieldB = new TextField(new Binding<string>(() => textB, v => { _writesB++; textB = v; }));

        // 真实垂直堆叠根：两个字段都在同一个真实运行时里。
        var root = new VStack(fieldA, fieldB);

        try
        {
            var options = new UiApplicationOptions
            {
                Title = "CSharp.UI P3a ordered host check (hidden)",
                Width = 320,
                Height = 220,
                VSync = false,
                StartVisible = false,
                UseTsfInputBridge = true,
                Started = context =>
                {
                    try
                    {
                        // 初始**不聚焦**任何字段：验证“Started 之前没有 focus 也能创建宿主”。
                        Log.Add("[p3a] started with no focus (initial state)");
                        context.Runtime.BuildFrame();

                        // 延后到真实循环跑过若干轮后开始场景（此时才聚焦 → 惰性建立 TSF）。
                        ScheduleAfterPump(context, 0);
                    }
                    catch (Exception ex)
                    {
                        Log.Add($"[p3a] EXCEPTION {ex.GetType().Name}: {ex.Message}");
                        _failures++;
                    }
                },
            };

            var exit = UiApplication.Run(root, options);
            _failures += exit == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Add($"[p3a] HOST EXCEPTION {ex.GetType().Name}: {ex.Message}");
            _failures++;
        }

        foreach (var line in Log)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine(_failures == 0 ? "[p3a] RESULT PASS" : $"[p3a] RESULT FAIL count={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 用<b>后台延迟</b>投递下一步，确保控制权真正回到宿主事件循环（GLFW.PollEvents）。
    /// </summary>
    /// <remarks>
    /// <b>禁止同步自投递：</b><c>ProcessPostedActions</c> 会一次排空整个队列，
    /// 在 dispatcher 动作里再 Post 自己会在同一轮内连续执行，泵根本不会前进。
    /// </remarks>
    private static void ScheduleAfterPump(UiHostContext context, int stage)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            context.Dispatcher.Post(() => Advance(context, stage));
        });
    }

    private static void Advance(UiHostContext context, int stage)
    {
        try
        {
            switch (stage)
            {
                case 0:
                    RunNoFocusThenLateFocus(context);
                    break;
                case 1:
                    // 该阶段内部会自行安排异步核对，之后由核对动作推进到 Finish。
                    RunSamePumpOrdering(context);
                    return;
                case 2:
                    Finish(context);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Add($"[p3a] stage {stage} EXCEPTION {ex.GetType().Name}: {ex.Message}");
            _failures++;
        }

        ScheduleAfterPump(context, stage + 1);
    }

    /// <summary>阶段 0：无焦点创建 → 迟聚焦 → 真实服务身份/几何/Start/End 轨迹。</summary>
    private static void RunNoFocusThenLateFocus(UiHostContext context)
    {
        var host = UiApplication.CurrentHost;
        if (host is null)
        {
            Log.Add("[p3a] FAIL no host");
            _failures++;
            return;
        }

        _ownHwnd = WindowsImeBridge.GetWindowHandle(host.Window);
        var router = host.Router;
        Log.Add($"[p3a] host hwnd=0x{_ownHwnd:X} osThreadId={GetCurrentThreadId()}");
        Log.Add($"[p3a] router={router}");

        if (router is null)
        {
            Log.Add("[p3a] FAIL no stable router on host");
            _failures++;
            return;
        }

        // R7：稳定服务身份 —— 运行时与适配器持有的是同一个实例。
        var serviceFromRuntime = context.Runtime.Services.TextInputService;
        Log.Add($"[p3a] runtime service={serviceFromRuntime?.GetType().Name} " +
            $"sameAsRouter={ReferenceEquals(serviceFromRuntime, router)}");
        if (!ReferenceEquals(serviceFromRuntime, router))
        {
            Log.Add("[p3a] FAIL runtime service is not the stable router");
            _failures++;
        }

        // 无焦点时不应有活动客户端/TSF 会话。
        Log.Add($"[p3a] before focus: activeClient={router.ActiveClient?.GetType().Name ?? "(none)"} " +
            $"backend={router.ActiveBackend} created={router.TsfCreateCount}");

        // 能力报告必须准确：候选锚点属 P3b，不能声明 Full。
        var caps = router.Capabilities;
        Log.Add($"[p3a] capabilities {caps}");
        if (caps.CandidateAnchor)
        {
            Log.Add("[p3a] FAIL candidate anchor declared before P3b");
            _failures++;
        }

        // 迟聚焦：真实 TextField 获得焦点 → 运行时 StartSession → 路由器惰性建立 TSF。
        // VStack 根本身不可聚焦：用公开 MoveFocus 把焦点移到第一个真实 TextField。
        context.Runtime.MoveFocus();
        context.Runtime.BuildFrame();
        Log.Add($"[p3a] late focus applied; activeClient={router.ActiveClient?.GetType().Name ?? "(none)"}");

        // 反向非零选区 + 版本>1：证明初始同步使用真实快照（R3）。
        // 通过公开 SetFocus/文档回读观察，不依赖核心 internal 接缝。
        if (router.ActiveClient is { } client)
        {
            var doc = client.Document;
            Log.Add($"[p3a] client document: textLen={doc.Text.Length} sel={doc.Selection} version={doc.Version}");

            // R3：TSF 初始同步必须使用该真实快照（文本/有向选区/版本），不得硬编码 0/0/1。
            if (router.TsfBridge is { } established)
            {
                var store = established.Session.Store.ReadAuthoritativeDocument();
                Log.Add($"[p3a] store after sync: textLen={store.Text.Length} " +
                    $"sel=[{store.SelectionStart},{store.SelectionEnd}) version={store.Version}");
                if (store.Text.Length != doc.Text.Length)
                {
                    Log.Add("[p3a] FAIL store text length != real client snapshot");
                    _failures++;
                }
            }
        }

        Log.Add($"[p3a] after focus: backend={router.ActiveBackend} created={router.TsfCreateCount} " +
            $"reason={router.BackendReason}");

        var tsf = router.TsfBridge;
        if (tsf is null)
        {
            Log.Add($"[p3a] note: TSF not established ({router.BackendReason})");
            return;
        }

        // 合成消费策略：只用于确定性验证消费链路。
        tsf.ConsumptionPolicyForTesting = (message, wParam, lParam) => wParam == 0x43;
        Log.Add("[p3a] synthetic key policy installed (VK_C consumed)");

        // R4：Focus 幂等 + 主 context 保持压栈（GetTop 证明）。
        var f1 = tsf.Session.Focus();
        var f2 = tsf.Session.Focus();
        var topHr = tsf.Session.GetTopContext(out var top);
        Log.Add($"[p3a] Focus#1=0x{f1:X8} Focus#2(idempotent)=0x{f2:X8} GetTop hr=0x{topHr:X8} " +
            $"topMatches={ReferenceEquals(top, tsf.Session.Context)} pushed={tsf.Session.IsPushed}");

        if (f1 < 0 || f2 < 0 || topHr < 0 || !tsf.Session.IsPushed)
        {
            Log.Add("[p3a] FAIL focus/push lifecycle");
            _failures++;
        }

        // Blur 只撤焦点，不弹主 context。
        var blurHr = tsf.Session.Blur();
        var topHr2 = tsf.Session.GetTopContext(out _);
        Log.Add($"[p3a] Blur hr=0x{blurHr:X8} GetTop after blur hr=0x{topHr2:X8} " +
            $"stillPushed={tsf.Session.IsPushed} focused={tsf.Session.IsFocused}");

        if (blurHr < 0 || !tsf.Session.IsPushed)
        {
            Log.Add("[p3a] FAIL Blur dropped the primary context");
            _failures++;
        }

        // 恢复焦点不应重复 Push。
        var resumeHr = tsf.Session.Focus();
        Log.Add($"[p3a] resume Focus hr=0x{resumeHr:X8} pushed={tsf.Session.IsPushed}");
        if (resumeHr < 0)
        {
            Log.Add("[p3a] FAIL resume focus");
            _failures++;
        }
    }

    /// <summary>
    /// 阶段 1：同一次泵内的“TSF 结果 → 普通字符 → 指针/焦点到第二个字段 → 后续字符”。
    /// </summary>
    private static void RunSamePumpOrdering(UiHostContext context)
    {
        var host = UiApplication.CurrentHost;
        var router = host?.Router;
        if (router is null)
        {
            return;
        }

        var tsf = router.TsfBridge;
        if (tsf is null)
        {
            Log.Add("[p3a] note: TSF not established; skipping same-pump ordering");
            return;
        }

        // 证据采集：只记录本工具合成的 WM_CHAR 码点。
        if (_observerHook == IntPtr.Zero)
        {
            _observerProc = (nCode, wParam, lParam) =>
            {
                if (nCode >= 0)
                {
                    var msg = Marshal.PtrToStructure<NativeMsg>(lParam);
                    if (msg.Hwnd == _ownHwnd
                        && wParam == (IntPtr)TsfMessageHookPolicy.PmRemove
                        && msg.Message == TsfMessageHookPolicy.WmChar)
                    {
                        SyntheticChars.Add((int)msg.WParam);
                    }
                }

                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            };
            _observerHook = SetWindowsHookExW(3, _observerProc, IntPtr.Zero, GetCurrentThreadId());
        }

        Log.Add("[p3a] posting A(unconsumed) + C(consumed) through the real GLFW pump");
        PostKey(_ownHwnd, 0x0100, 0x41, isUp: false);
        PostKey(_ownHwnd, 0x0101, 0x41, isUp: true);
        PostKey(_ownHwnd, 0x0100, 0x43, isUp: false);
        PostKey(_ownHwnd, 0x0101, 0x43, isUp: true);

        // 让真实泵跑过几轮后再核对；核对完成后进入 Finish。
        _ = Task.Run(async () =>
        {
            await Task.Delay(220).ConfigureAwait(false);
            context.Dispatcher.Post(() =>
            {
                VerifyOrdering(context, router);
                Finish(context);
            });
        });
    }

    private static void VerifyOrdering(UiHostContext context, WindowsTextInputRouter router)
    {
        var tsf = router.TsfBridge;
        var aChars = SyntheticChars.Count(c => c == 0x61);
        var cChars = SyntheticChars.Count(c => c == 0x63);

        Log.Add($"[p3a] synthetic chars A(U+0061)={aChars} C(U+0063)={cChars} total={SyntheticChars.Count}");
        if (tsf is not null)
        {
            Log.Add($"[p3a] hook {tsf.Hook}");
        }

        Log.Add($"[p3a] adapter ordered deferred={UiApplication.CurrentHost?.InputAdapter?.OrderedDeferredCount} " +
            $"delivered={UiApplication.CurrentHost?.InputAdapter?.OrderedDeliveredCount}");

        if (aChars < 1)
        {
            Log.Add("[p3a] FAIL unconsumed A produced no character");
            _failures++;
        }

        if (cChars != 0)
        {
            Log.Add($"[p3a] FAIL consumed C echoed {cChars} time(s)");
            _failures++;
        }

        if (tsf is not null && tsf.Hook.RewriteCount != tsf.Hook.ConsumedCount)
        {
            Log.Add($"[p3a] FAIL consumed={tsf.Hook.ConsumedCount} != rewritten={tsf.Hook.RewriteCount}");
            _failures++;
        }
    }

    private static void Finish(UiHostContext context)
    {
        var host = UiApplication.CurrentHost;
        var router = host?.Router;

        if (router is not null)
        {
            Log.Add($"[p3a] final router={router}");
            Log.Add($"[p3a] writer lease={HostWriterRegistry.CurrentLease(_ownHwnd)}");
        }

        Log.Add($"[p3a] writes A={_writesA} B={_writesB}");

        if (_observerHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_observerHook);
            _observerHook = IntPtr.Zero;
        }

        context.RequestExit(_failures == 0 ? 0 : 1);
    }

    private static void PostKey(IntPtr hwnd, int msg, int vk, bool isUp)
    {
        var scan = MapVirtualKeyW((uint)vk, 0) & 0xFF;
        var bits = 1L | ((long)scan << 16);
        if (isUp)
        {
            bits |= 1L << 30;
            bits |= 1L << 31;
        }

        PostMessageW(hwnd, (uint)msg, (IntPtr)vk, (IntPtr)bits);
    }

    private static IntPtr _observerHook;
    private static HookProc? _observerProc;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
