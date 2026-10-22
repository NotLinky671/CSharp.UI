using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a 正式 TSF 会话的<b>真实</b>生命周期（隐藏 HWND + 自建 STA 线程 + 真实 TextField 客户端）。
/// </summary>
/// <remarks>
/// <para>
/// <b>覆盖的静态缺陷回归：</b>
/// </para>
/// <list type="number">
/// <item><description>
/// 构造函数必须真正持有写入来源租约：释放后同一 HWND 必须能建立<b>第二个</b>会话。
/// </description></item>
/// <item><description>
/// 成功的 <c>Activate</c> 必须转移到实例：释放路径必须真正 <c>Deactivate</c>（而不是只记在局部变量里）。
/// </description></item>
/// <item><description>
/// 释放必须先真正撤销焦点再标记 disposed，否则 <c>Blur</c> 直接返回、焦点从未清掉。
/// </description></item>
/// <item><description>
/// 主 context 只能在会话卸载时 <c>Pop(TF_POPF_ALL)</c>，且真实 HRESULT 必须被记录。
/// </description></item>
/// </list>
/// <para>
/// <b>不使用 null/fake 客户端绕过：</b>客户端是真实 <see cref="TextField"/> 经真实
/// <see cref="UiRuntime"/> 建立的 <see cref="ITextInputClient"/>，文本输入服务是真实转发实现。
/// </para>
/// <para>
/// <b>不做的事：</b>不显示窗口、不注入键盘、不改全局输入法配置、不夺取前台焦点；
/// 不做真实微软拼音验收（P4），不声明候选呈现（P3b）。
/// </para>
/// <para>
/// <b>绝不把“没有抛异常”当成功：</b>每个原生调用都读取真实 HRESULT 并作为日志/断言依据；
/// 只断言“状态与真实原生读回一致”，不断言本环境必然 S_OK。
/// </para>
/// </remarks>
public sealed class TsfSessionLifetimeTests
{
    private const int WS_OVERLAPPED = 0x00000000;

    [Fact]
    public void 真实隐藏窗口上会话焦点生命周期与第二会话成对释放()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：没有可调用的 ole32/msctf，真实生命周期不适用。
            return;
        }

        var failures = new List<string>();
        var log = new List<string>();
        var thread = new Thread(() => RunSessionLifetimeProbe(failures, log));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "TSF 会话生命周期探测超时。");

        foreach (var line in log)
        {
            Console.WriteLine(line);
        }

        Assert.True(failures.Count == 0, "TSF 会话生命周期失败：" + string.Join(" | ", failures));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunSessionLifetimeProbe(List<string> failures, List<string> log)
    {
        var className = $"CSharp.UI.P3a.SessionLifetime.{Environment.ProcessId}";
        var instance = GetModuleHandleW(null);

        WndProcDelegate proc = (hWnd, msg, wParam, lParam) => DefWindowProcW(hWnd, msg, wParam, lParam);
        var procPtr = Marshal.GetFunctionPointerForDelegate(proc);

        var wc = new WndClassW
        {
            WndProc = procPtr,
            Instance = instance,
            ClassName = className,
        };

        if (RegisterClassW(ref wc) == 0)
        {
            failures.Add($"RegisterClassW 失败 Win32={Marshal.GetLastWin32Error()}");
            return;
        }

        var hwnd = IntPtr.Zero;
        UiRuntime? runtime = null;
        TsfHostSession? first = null;
        TsfHostSession? second = null;

        try
        {
            // 隐藏窗口：只作为 HWND 与 store 关联对象，不调用 ShowWindow。
            hwnd = CreateWindowExW(0, className, "CSharp.UI P3a session lifetime (hidden)", WS_OVERLAPPED,
                0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                failures.Add($"CreateWindowExW 失败 Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            // 真实 TextField 权威客户端（不是 fake）。
            var text = "ABCD";
            var service = new ForwardingService();
            runtime = new UiRuntime(
                new TextField(new Binding<string>(() => text, v => text = v)),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
                    TextureService = new RecordingTextureService(),
                    TextInputService = service,
                });
            runtime.SetViewport(new Size(400, 120), 1.0);
            runtime.Start();
            runtime.BuildFrame();
            runtime.SetFocus(runtime.Root);
            runtime.BuildFrame();

            var client = service.ActiveClient;
            if (client is null)
            {
                failures.Add("真实 TextField 未建立文本输入客户端（无法在不使用 fake 的前提下继续）。");
                return;
            }

            // 单一写入来源：本窗口此刻必须空闲。
            if (HostWriterRegistry.CurrentOwner(hwnd) is not null)
            {
                failures.Add($"前置错误：窗口已被 {HostWriterRegistry.CurrentLease(hwnd)} 占用。");
                return;
            }

            // ---------------- 第一个会话 ----------------
            var candidateOverlay = new CandidateOverlay();
            var create1 = TsfHostSession.TryCreate(
                hwnd, client, c => ReferenceEquals(c, service.ActiveClient), text,
                candidateOverlay, () => { }, out first);
            log.Add($"[life] TryCreate#1 succeeded={create1.Succeeded} failure={create1.Failure} reason={create1.Reason}");
            if (!create1.Succeeded || first is null)
            {
                failures.Add($"TSF 会话建立失败：{create1.Failure} {create1.Reason}");
                return;
            }

            var lease1 = HostWriterRegistry.CurrentLease(hwnd);
            log.Add($"[life] lease after create#1 = {lease1?.ToString() ?? "(none)"}");
            if (lease1 is null || lease1.Kind != HostWriterKind.Tsf)
            {
                failures.Add("建立成功后窗口未被本 TSF 会话占用（写入来源租约缺失）。");
                return;
            }

            // 焦点幂等：第二次 Focus 必须命中幂等路径，不重复 Push/SetFocus。
            var focus1 = first.Focus();
            var focus2 = first.Focus();
            log.Add($"[life] Focus#1 hr=0x{focus1:X8} Focus#2 hr=0x{focus2:X8} "
                + $"alreadyApplied={first.FocusAlreadyAppliedCount} pushed={first.IsPushed} focused={first.IsFocused}");

            if (focus1 < 0)
            {
                failures.Add($"首个 Focus 失败：{TsfHResult.Describe(focus1)}");
                return;
            }

            if (focus2 != TsfHResult.S_OK || first.FocusAlreadyAppliedCount != 1)
            {
                failures.Add($"重复 Focus 未走幂等路径：hr=0x{focus2:X8} "
                    + $"alreadyApplied={first.FocusAlreadyAppliedCount}。");
            }

            if (!first.IsFocused || !first.IsPushed)
            {
                failures.Add($"Focus 后状态不符：focused={first.IsFocused} pushed={first.IsPushed}。");
            }

            var topHr = first.GetTopContext(out var top);
            if (topHr < 0 || top is null)
            {
                failures.Add($"GetTop 失败：{TsfHResult.Describe(topHr)}");
            }
            else
            {
                log.Add($"[life] GetTop hr=0x{topHr:X8} "
                    + $"topIsOwnContext={ReferenceEquals(top, first.Context)}");
            }

            // 失焦：必须真的撤销焦点，且不弹主 context。
            var blurHr = first.Blur();
            log.Add($"[life] Blur hr=0x{blurHr:X8} ({TsfHResult.Describe(blurHr)}) "
                + $"focused={first.IsFocused} pushed={first.IsPushed} "
                + $"blurFailures={first.BlurFailureCount} foreignRejects={first.ForeignFocusRejectCount}");

            var focusNowAfterBlur = ReadThreadFocusOwnership(first);
            log.Add($"[life] thread focus ownership after Blur = {focusNowAfterBlur} "
                + $"(blurHr=0x{blurHr:X8} {TsfHResult.Describe(blurHr)})");

            // 状态必须与真实读回一致：不把失败当成已失焦，也不把成功当成仍聚焦。
            if (focusNowAfterBlur != TsfThreadFocusOwnership.Own && first.IsFocused)
            {
                failures.Add($"原生焦点已不属于本会话（{focusNowAfterBlur}），"
                    + "但 Blur 后 IsFocused 仍为 true（状态与真实读回不符）。");
            }

            if (focusNowAfterBlur == TsfThreadFocusOwnership.Own && !first.IsFocused && blurHr >= 0)
            {
                failures.Add("原生焦点仍属于本会话，但 Blur 报告成功且 IsFocused=false（谎报失焦）。");
            }

            if (focusNowAfterBlur is null)
            {
                failures.Add("失焦后无法读回真实 TSF 焦点归属（诊断入口失败）。");
            }

            if (!first.IsPushed)
            {
                failures.Add("Blur 弹掉了主 context（primary 只能在会话卸载时用 TF_POPF_ALL 移除）。");
            }

            // 恢复焦点：不得重复 Push。
            var resumeHr = first.Focus();
            log.Add($"[life] resume Focus hr=0x{resumeHr:X8} pushed={first.IsPushed} focused={first.IsFocused}");
            if (resumeHr < 0)
            {
                failures.Add($"恢复焦点失败：{TsfHResult.Describe(resumeHr)}");
            }

            // 跨线程释放：必须被拒绝，且不消耗任何资源（拥有线程仍能清理）。
            var crossThread = new Thread(() => first.Dispose());
            crossThread.Start();
            if (!crossThread.Join(TimeSpan.FromSeconds(30)))
            {
                failures.Add("跨线程 Dispose 未在超时内返回。");
            }

            var wrongThreadCount = first.WrongThreadDisposeCount;
            log.Add($"[life] cross-thread Dispose rejected={wrongThreadCount} "
                + $"disposed={first.IsDisposed} leaseStillHeld={HostWriterRegistry.CurrentLease(hwnd) is not null}");

            if (wrongThreadCount < 1)
            {
                failures.Add("跨线程 Dispose 未被拒绝（未计数）。");
            }

            if (first.IsDisposed)
            {
                failures.Add("跨线程 Dispose 错误地标记了已释放，拥有线程将无法再清理。");
            }

            // ---------------- 拥有线程释放第一个会话 ----------------
            first.Dispose();
            log.Add($"[life] Dispose#1 disposed={first.IsDisposed} leaseReleased={first.WriterLeaseReleased} "
                + $"pushed={first.IsPushed} teardownPopHr=0x{first.TeardownPopHResult:X8} "
                + $"popFailures={first.TeardownPopFailureCount} unadviseFailures={first.UnadviseFailureCount}");

            if (!first.IsDisposed)
            {
                failures.Add("拥有线程 Dispose 未完成释放。");
            }

            if (!first.WriterLeaseReleased)
            {
                failures.Add("释放未归还写入来源租约（构造函数未持有租约的静态缺陷回归）。");
            }

            if (HostWriterRegistry.CurrentLease(hwnd) is not null)
            {
                failures.Add($"释放后窗口仍被占用：{HostWriterRegistry.CurrentLease(hwnd)}。");
            }

            if (first.IsPushed)
            {
                failures.Add("会话卸载后主 context 仍报告压栈（Pop 未生效）。");
            }

            // 幂等：重复释放不再触碰任何原生对象。
            first.Dispose();
            log.Add($"[life] Dispose#1 repeated; wrongThread={first.WrongThreadDisposeCount}");

            // ---------------- 同一 HWND 的第二个会话 ----------------
            // 这一步只有在写入来源被真正归还后才可能成功，是“租约成对”的真实证据。
            var create2 = TsfHostSession.TryCreate(
                hwnd, client, c => ReferenceEquals(c, service.ActiveClient), text,
                candidateOverlay, () => { }, out second);
            log.Add($"[life] TryCreate#2 succeeded={create2.Succeeded} failure={create2.Failure} reason={create2.Reason}");
            if (!create2.Succeeded || second is null)
            {
                failures.Add($"同一 HWND 无法建立第二个 TSF 会话：{create2.Failure} {create2.Reason}");
                return;
            }

            if (ReferenceEquals(first, second))
            {
                failures.Add("第二个会话返回了同一个实例，未真正重建。");
                return;
            }

            if (ReferenceEquals(HostWriterRegistry.CurrentLease(hwnd), lease1))
            {
                failures.Add("第二个会话复用了已释放的旧租约身份。");
            }

            var secondFocus = second.Focus();
            log.Add($"[life] second Focus hr=0x{secondFocus:X8} pushed={second.IsPushed} focused={second.IsFocused}");
            if (secondFocus < 0)
            {
                failures.Add($"第二个会话 Focus 失败：{TsfHResult.Describe(secondFocus)}");
            }

            // 幂等：已释放的旧实例再次 Blur 必须直接返回 S_OK，不触碰任何原生对象，
            // 因此不可能清掉第二个会话刚取得的焦点。
            var staleBlur = first.Blur();
            var staleDisposed = first.IsDisposed;
            var focusAfterStaleBlur = ReadThreadFocusOwnership(second);
            log.Add($"[life] stale Blur hr=0x{staleBlur:X8} "
                + $"(first disposed={staleDisposed}) secondFocusOwnership={focusAfterStaleBlur} "
                + $"secondFocused={second.IsFocused}");

            if (staleBlur != TsfHResult.S_OK)
            {
                failures.Add($"已释放实例的 Blur 未幂等成功：{TsfHResult.Describe(staleBlur)}。");
            }

            if (focusAfterStaleBlur is not (TsfThreadFocusOwnership.Own or null) && second.IsFocused)
            {
                failures.Add($"已释放的旧会话清掉了第二个会话的 TSF 焦点（现在={focusAfterStaleBlur}）。");
            }

            second.Dispose();
            log.Add($"[life] Dispose#2 disposed={second.IsDisposed} leaseReleased={second.WriterLeaseReleased} "
                + $"pushed={second.IsPushed} teardownPopHr=0x{second.TeardownPopHResult:X8} "
                + $"popFailures={second.TeardownPopFailureCount}");

            if (!second.WriterLeaseReleased || HostWriterRegistry.CurrentLease(hwnd) is not null)
            {
                failures.Add("第二个会话释放后写入来源未归还。");
            }

            log.Add($"[life] final registeredWindows={HostWriterRegistry.RegisteredWindowCount}");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 尽力清理：真实会话必须在拥有线程上释放。
            try
            {
                first?.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                second?.Dispose();
            }
            catch (Exception)
            {
            }

            runtime?.Dispose();

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
            }

            UnregisterClassW(className, instance);
            // 不调用 HostWriterRegistry.ResetForTesting：那会清掉其它并行测试的真实持有者，
            // 也会掩盖本测试自身的泄漏。本测试只 Dispose 自己创建的资源，
            // 并已在上方断言本 HWND 的租约确实已归还。
        }
    }

    /// <summary>
    /// 只读线程 TSF 文档焦点归属；<c>null</c> 表示查询失败、非拥有线程或已释放。
    /// </summary>
    /// <remarks>委托给会话自身的只读入口，绝不新建线程管理器或做 <c>SetFocus</c>。</remarks>
    private static TsfThreadFocusOwnership? ReadThreadFocusOwnership(TsfHostSession session)
        => session.ThreadFocusOwnership;

    private sealed class ForwardingService : ITextInputService
    {
        // 组合输入已接入；候选呈现属 P3b，因此不声明 Full。
        public TextInputCapabilities Capabilities
            => new(committedText: true, composition: true, candidateAnchor: false,
                reason: "测试用转发服务：组合已接入，候选呈现属 P3b。");

        public ITextInputClient? ActiveClient { get; private set; }

        public bool StartSession(ITextInputClient client)
        {
            ActiveClient = client;
            return true;
        }

        public bool EndSession()
        {
            if (ActiveClient is null)
            {
                return false;
            }

            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");

        public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

        public bool PublishGeometry(TextInputGeometry geometry) => false;
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
