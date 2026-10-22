using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

internal sealed class ScenarioOptions
{
    internal required string OutDir { get; init; }

    internal string? Scenario { get; init; }

    internal int TimeoutMs { get; init; } = 300_000;

    internal int ForegroundWaitMs { get; init; } = 180_000;

    internal bool SuppressCandidateUi { get; init; }

    internal int ScrollDelta { get; init; } = 7;

    internal int WindowWidth { get; init; } = 760;

    internal int WindowHeight { get; init; } = 420;

    internal bool Sta { get; init; }

    /// <summary>隐藏宿主启动路径回归（R17）：不显示窗口、不请求前台、不注入按键、不跑场景。</summary>
    internal bool HiddenHost { get; init; }

    /// <summary>context/view 只读几何对照探针（默认关闭；仅显式 --probe-context-view 启用）。</summary>
    internal bool ProbeContextView { get; init; }

    /// <summary>
    /// 布局调用链诊断（默认关闭；仅显式 --trace-layout-chain 启用）。
    /// 只增加配对通知 ID、锁请求/授予、归因标签与只读焦点快照，不改变任何行为。
    /// </summary>
    internal bool TraceLayoutChain { get; init; }

    /// <summary>
    /// R26：改用 <c>ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)</c> 激活。
    /// 默认 false，完全保留既有 <c>Activate</c> 行为；只切换激活方式，无其他行为变量。
    /// </summary>
    internal bool UiElementEnabledOnly { get; init; }

    /// <summary>
    /// R29B opt-in 真实单行输入 child（默认 false）：root-&gt;viewport-&gt;input 三层真实窗口
    /// 接管 TSF 关联/GetWnd/绘制/键符路由/焦点；root 仍是前台/捕获/调度/生命周期句柄。
    /// 本步只接线 child 创建与 hidden-host 回归；可见场景在解析时即被拒绝，
    /// 因此本标志为 true 时只允许 <see cref="HiddenHost"/> 路径。
    /// </summary>
    internal bool ChildInputSurface { get; init; }

    /// <summary>
    /// R30 有界实验（默认 false）：只在 <c>--child-input-surface --scenario clip-out</c>
    /// 且显式 <c>--child-hide-when-clipped</c> 时，clip 几何严格全零验证后、clipped 截图前
    /// 对自有 input child 做一次 NOACTIVATE 可见性变更，恢复时再写回原可见性。
    /// 任何焦点/组合/前台变化即如实失败，不 SetFocus、不重关联 TSF、不取消/重打。
    /// 所有其他场景/默认矩阵/hidden-host/冲突实验一律在解析期拒绝。
    /// </summary>
    internal bool ChildHideWhenClipped { get; init; }

    /// <summary>
    /// R31 有界实验（默认 false）：只在 <c>--child-input-surface --scenario clip-out</c>
    /// 且显式 <c>--child-suspend-tsf-focus-when-clipped</c> 时，clip 几何严格全零验证后、
    /// clipped 截图前经真实 <c>ITfThreadMgr.SetFocus(null)</c> 挂起自有 TSF 文档焦点，
    /// 恢复时把保存的同一 doc/context 重新 SetFocus。与 R30 互斥；Win32 前台/焦点/
    /// 关联保持，不做 HWND SetFocus/AssociateFocus/Pop/Push、不结束组合、不重输。
    /// </summary>
    internal bool ChildSuspendTsfFocusWhenClipped { get; init; }

    /// <summary>
    /// R34 有界实验（默认 false）：只在 <c>--child-input-surface --scenario scroll-follow|clip-out
    /// --apartment sta</c> 且显式 <c>--host-drawn-candidates</c> 时启用。候选专属
    /// <c>BeginUIElement</c> 返回 <c>pbShow=FALSE</c>（UiElementEnabledOnly 激活由本模式内部
    /// 配置），Update 时查询真实 <c>ITfCandidateListUIElement</c> 生成有界不可变快照，
    /// 由自有 root 客户区 GDI overlay 绘制；End/焦点丢失即清屏，绝不 Show(TRUE)、
    /// 绝不重打/SetFocus(null) 恢复。与 R30/R31/显式抑制/显式 UiElementEnabledOnly 互斥。
    /// </summary>
    internal bool HostDrawnCandidates { get; init; }

    /// <summary>
    /// R37 有界实验（默认 false）：只在 <c>--host-drawn-candidates --child-input-surface
    /// --scenario clip-out --apartment sta</c> 且显式 <c>--verify-restored-commit</c> 时启用。
    /// 在执行器全部恢复证据成立之后、清理组合之前，对恢复后的<b>当前</b>候选做一次真实
    /// Space 提交证明：组合必须恰好结束一次、文档文本精确「你好」、绑定增量精确 1
    /// （文本/来源正确）、presenter 清空且 overlay 真实不可见。默认 off 时 R36 证明路径
    /// 逐字节不变（不多发任何键、不多做任何查询）。
    /// </summary>
    internal bool VerifyRestoredCommit { get; init; }
}

/// <summary>
/// 真实场景驱动。只在确认自有窗口前台后注入真实扫描码键；不合成 IME 消息、不模拟鼠标、不抢前台。
/// 候选是否实际移动/隐藏由截图核对，脚本只记录几何与 API 结果。
/// </summary>
internal sealed class ScenarioRunner
{
    private const ushort VkSpace = 0x20;
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;

    private static readonly string[] DefaultScenarios =
    {
        "nihao-commit",
        "escape-cancel",
        "enter-select",
        "scroll-follow",
        "move-window",
        "resize",
        "clip-out",
        "blur-close",
    };

    private static readonly string[] DiagnosticScenarios =
    {
        "uielement-hide-probe",
        "native-ui-lifecycle",
        "ascii-plain",
        "unicode-direct",
    };

    private readonly TsfHostWindow _window;
    private readonly AcpTextStore _store;
    private readonly BindingRecorder _recorder;
    private readonly TsfSessionHost _session;
    private readonly ProbeLog _log;
    private readonly TsfTrace _trace;
    private readonly ScenarioOptions _options;
    private readonly InputScript _input;
    private ActiveProfileSnapshot? _scenarioIdentity;
    private string _currentScenario = "(none)";

    internal ScenarioRunner(TsfHostWindow window, AcpTextStore store, BindingRecorder recorder,
        TsfSessionHost session, ProbeLog log, TsfTrace trace, ScenarioOptions options)
    {
        _window = window;
        _store = store;
        _recorder = recorder;
        _session = session;
        _log = log;
        _trace = trace;
        _options = options;
        _input = new InputScript(log, window.Hwnd, "tsf-prototype")
        {
            UseScanCodes = true,
        };

        // R29B opt-in：一次 UI Invoke 的逐键前置守卫。root/input 必须是本进程本 UI 线程的
        // 存活窗口、前台必须是 root、键盘焦点必须是 input——任一项不符即拒绝本次发送，
        // 且守卫在 nihao 与 cleanup Esc 全路径上始终生效（fail closed）。默认 off 不设置。
        if (options.ChildInputSurface)
        {
            _input.BeforeSendGuard = () => window.Invoke(ChildSurfaceSendGuard);
        }
    }

    /// <summary>
    /// opt-in <see cref="InputScript.BeforeSendGuard"/>：单个 UI Invoke 内核对全部
    /// 归属/前台/焦点事实；返回 null 允许发送，返回拒绝原因即 fail closed。
    /// 不夺焦、不 SetForegroundWindow、不 AttachThreadInput——只读核对，拒绝即停。
    /// </summary>
    private string? ChildSurfaceSendGuard()
    {
        var root = _window.Hwnd;
        var input = _window.InputHwnd;
        var ownTid = NativeMethods.CurrentWin32ThreadId();
        var ownPid = Environment.ProcessId;

        if (root == IntPtr.Zero || input == IntPtr.Zero
            || !NativeMethods.IsWindow(root) || !NativeMethods.IsWindow(input))
        {
            return "own root/input hwnd not valid";
        }

        var rootTid = NativeMethods.GetWindowThreadProcessId(root, out var rootPid);
        var inputTid = NativeMethods.GetWindowThreadProcessId(input, out var inputPid);
        if (rootPid != ownPid || inputPid != ownPid)
        {
            return $"hwnd owned by foreign process (root_pid={rootPid} input_pid={inputPid} own={ownPid})";
        }

        if (rootTid != ownTid || inputTid != ownTid)
        {
            return $"hwnd owned by foreign ui thread (root_tid={rootTid} input_tid={inputTid} own={ownTid})";
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != root)
        {
            return $"own root foreground lost (foreground=0x{foreground.ToInt64():X})";
        }

        var focus = NativeMethods.GetFocus();
        if (focus != input)
        {
            return $"input focus not on input surface (focus=0x{focus.ToInt64():X})";
        }

        return null;
    }

    /// <summary>
    /// R35：仅在 <c>--host-drawn-candidates</c> 下运行的有界只读就绪等待。
    /// 每轮在 UI 线程派发同一个 <see cref="HostDrawnProfileReadiness.PollOnceOnUiThread"/>
    /// body：guard-before → 只读 <c>TsfProfile.ReadActiveProfile</c> → guard-after；
    /// runner 线程只负责 ~200ms 节奏与 bound，UI 线程不阻塞。就绪后原
    /// <see cref="PrepareComposition"/> 严格门禁照旧执行；超时如实失败。
    /// </summary>
    private bool WaitForMicrosoftPinyinProfileReady(int timeoutMs)
    {
        var root = _window.Hwnd;
        var input = _window.InputHwnd;
        var uiTid = _window.UiThreadId;
        var pid = Environment.ProcessId;
        return HostDrawnProfileReadiness.Run(
            invokeOnUiThread: action => _window.Invoke(action)!,
            readFacts: ReadHostDrawnGuardFacts,
            checkGuard: facts => HostDrawnProfileReadiness.CheckGuard(
                facts, root, input, uiTid, pid),
            poll: () => HostDrawnProfileReadiness.PollOnceOnUiThread(
                ReadHostDrawnGuardFacts, ReadActiveProfileIdentity,
                root, input, uiTid, pid),
            timeoutMs: timeoutMs,
            trace: (kind, fields) => _trace("scenario", kind, fields));
    }

    /// <summary>UI 线程内采样一次原生守卫事实（root/input 归属 + 前台/焦点 + 锁/重入）。</summary>
    private HostDrawnProfileReadiness.GuardFacts ReadHostDrawnGuardFacts()
    {
        var root = _window.Hwnd;
        var input = _window.InputHwnd;
        var foreground = NativeMethods.GetForegroundWindow();
        var focus = NativeMethods.GetFocus();
        var rootValid = root != IntPtr.Zero && NativeMethods.IsWindow(root);
        var inputValid = input != IntPtr.Zero && NativeMethods.IsWindow(input);
        uint rootTid = 0, rootPid = 0, inputTid = 0, inputPid = 0;
        if (root != IntPtr.Zero)
        {
            rootTid = NativeMethods.GetWindowThreadProcessId(root, out rootPid);
        }

        if (input != IntPtr.Zero)
        {
            inputTid = NativeMethods.GetWindowThreadProcessId(input, out inputPid);
        }

        var store = _store.SnapshotForLifecycle();
        return new HostDrawnProfileReadiness.GuardFacts(
            rootValid, inputValid, rootTid, rootPid, inputTid, inputPid,
            foreground, focus, store.LockType, _store.LayoutNotifyDepth);
    }

    /// <summary>UI 线程内只读活动 profile 身份（经既有 <c>TsfProfile.ReadActiveProfile</c>）。</summary>
    private (int HResult, uint ProfileType, ushort LangId, Guid Clsid, Guid ProfileGuid)
        ReadActiveProfileIdentity()
    {
        var (hr, profile) = TsfProfile.ReadActiveProfile(_log, "host-drawn-readiness");
        return (hr, profile.ProfileType, profile.LangId, profile.Clsid, profile.ProfileGuid);
    }

    internal static bool IsKnownScenario(string name)
        => DefaultScenarios.Contains(name, StringComparer.Ordinal)
            || DiagnosticScenarios.Contains(name, StringComparer.Ordinal);

    internal int Run()
    {
        // 只做一次普通前台请求；之后等待用户点击自有窗口，绝不 TOPMOST/AttachThreadInput/模拟点击。
        NativeMethods.SetForegroundWindow(_window.Hwnd);
        if (!WaitOwnForeground(_options.ForegroundWaitMs))
        {
            _trace("scenario", "foreground-missing", new Dictionary<string, object?>
            {
                ["waited_ms"] = _options.ForegroundWaitMs,
                ["foreground"] = DescribeForeground(),
            });
            return 2;
        }

        // R29B opt-in：在**唯一一次** root 前台授权成立之后，且在任何 opt-in 可见几何
        // 场景之前，按已审核的 InitializeInputFocus 路径把焦点一次性交给真实 input child。
        // 拒绝（非本进程/UI 线程、前台不在 root、SetFocus 失败）即如实失败，绝不重试夺焦。
        if (_options.ChildInputSurface
            && _options.Scenario is "scroll-follow" or "clip-out" or "move-window")
        {
            if (!_window.InitializeInputFocus())
            {
                _trace("scenario", "input-focus-init-failed", new Dictionary<string, object?>
                {
                    ["scenario"] = _options.Scenario,
                    ["outcome"] = _window.InputFocusInitOutcome,
                });
                return 2;
            }
        }

        // R35：仅显式 --host-drawn-candidates 下，InitializeInputFocus 之后、任何场景/输入
        // 之前做有界只读就绪等待——用户可在此期间手动切到 Microsoft Pinyin。程序绝不
        // 激活/切换/启用 profile、不发键、不抢焦点；外部前台/焦点时既不查询 COM 也不输入。
        // 超时如实退出，绝不把环境未就绪标通过；默认 off 由 RequiresWait 完全跳过。
        if (HostDrawnProfileReadiness.RequiresWait(_options)
            && !WaitForMicrosoftPinyinProfileReady(_options.ForegroundWaitMs))
        {
            _trace("scenario", "profile-readiness-timeout", new Dictionary<string, object?>
            {
                ["waited_ms"] = _options.ForegroundWaitMs,
            });
            return 2;
        }

        var scenarios = _options.Scenario is { } single ? new[] { single } : DefaultScenarios;
        var failed = 0;
        var pendingVisual = 0;
        var executed = 0;
        foreach (var name in scenarios)
        {
            if (_window.Hwnd == IntPtr.Zero)
            {
                _trace("scenario", "window-closed", new Dictionary<string, object?>
                {
                    ["scenario"] = name,
                });
                break;
            }

            executed++;
            if (!EnsureOwnForeground(15_000))
            {
                _trace("scenario", "result", new Dictionary<string, object?>
                {
                    ["scenario"] = name,
                    ["checks_pass"] = false,
                    ["candidate_visual"] = "not-required",
                    ["assessment"] = "fail",
                    ["pass"] = false,
                    ["reason"] = "own foreground not available",
                });
                failed++;
                continue;
            }

            var writesBefore = _recorder.Writes;
            _currentScenario = name;
            _scenarioIdentity = null;
            // R34D：per-scenario 候选基线每张都重新建立——绝不跨场景沿用旧 id/代数。
            _hostDrawnBaseline = null;
            var (ok, detail) = name switch
            {
                "nihao-commit" => RunNihaoCommit(writesBefore),
                "escape-cancel" => RunEscapeCancel(writesBefore),
                "enter-select" => RunEnterSelect(writesBefore),
                "scroll-follow" => _options.ChildInputSurface
                    ? RunChildScrollFollow(writesBefore)
                    : RunScrollFollow(writesBefore),
                "move-window" => _options.ChildInputSurface
                    ? RunChildMoveWindow(writesBefore)
                    : RunMoveWindow(writesBefore),
                "resize" => RunResize(writesBefore),
                "clip-out" => _options.ChildInputSurface
                    ? RunChildClipOut(writesBefore)
                    : RunClipOut(writesBefore),
                "uielement-hide-probe" => RunUiElementHideProbe(writesBefore),
                "native-ui-lifecycle" => RunNativeUiLifecycle(writesBefore),
                "ascii-plain" => RunAsciiPlain(writesBefore),
                "unicode-direct" => RunUnicodeDirect(writesBefore),
                "blur-close" => RunBlurClose(writesBefore),
                _ => (false, "unknown scenario"),
            };

            // 场景结束再只读记录一次身份；期间发生变化或查询不可用即不通过，绝不把结果泛称 Microsoft Pinyin。
            // opt-in 路径不用弱 TryReadActiveProfileSnapshot：统一走 safepoint 路由
            // （同一 UI 安全点内核对 foreground==root 且 focus==input 后才查询 profile）。
            if (name != "blur-close" && _scenarioIdentity is { } before && _window.Hwnd != IntPtr.Zero)
            {
                if (_options.ChildInputSurface)
                {
                    var post = ProbeLifecycleSafePointOnUiThread(writesBefore,
                        "post:" + name, false, out var postOk, out var postDetail);
                    if (!postOk)
                    {
                        ok = false;
                        detail += "; post safepoint failed: " + postDetail;
                    }
                    else if (!ImeIdentity.IsVerifiableMicrosoftPinyin(post.Profile))
                    {
                        ok = false;
                        detail += "; post identity unverifiable: " + post.Profile;
                    }
                }
                else if (TryReadActiveProfileSnapshot("post:" + name, out var after, out var postDetail))
                {
                    if (!ImeIdentity.SameIdentity(before, after))
                    {
                        ok = false;
                        detail += "; active profile changed during scenario";
                    }
                }
                else
                {
                    ok = false;
                    detail += "; post identity unavailable: " + postDetail;
                }
            }

            var outcome = new ScenarioOutcome(
                ok,
                RequiresVisualAssessment(name)
                    ? CandidateVisualStatus.Pending
                    : CandidateVisualStatus.NotRequired,
                detail);

            _trace("scenario", "result", new Dictionary<string, object?>
            {
                ["scenario"] = name,
                ["checks_pass"] = outcome.ChecksPass,
                ["candidate_visual"] = outcome.CandidateVisual == CandidateVisualStatus.Pending
                    ? "pending"
                    : "not-required",
                ["assessment"] = outcome.Assessment,
                ["pass"] = outcome.CompletePass,
                ["detail"] = outcome.Detail,
                ["writes_delta"] = _recorder.Writes - writesBefore,
                ["composing"] = _store.IsComposing,
                ["text"] = ImeFormat.EscapeUtf16(_store.Document.Text),
            });

            if (!outcome.ChecksPass)
            {
                failed++;
            }
            else if (outcome.CandidateVisual == CandidateVisualStatus.Pending)
            {
                pendingVisual++;
            }

            if (name == "blur-close")
            {
                break;
            }
        }

        return ScenarioAggregate.ResolveExitCode(failed, pendingVisual, executed);
    }

    private static bool RequiresVisualAssessment(string name)
        => name is "scroll-follow" or "move-window" or "resize" or "clip-out" or "uielement-hide-probe"
            or "native-ui-lifecycle";

    private (bool Ok, string Detail) RunNihaoCommit(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s1-composing");
        _input.PressVirtualKey(VkSpace, "space");
        var ended = WaitUntil(() => !_store.IsComposing, 4_000);
        Wait(400);
        Capture("s1-committed");

        var text = _store.Document.Text;
        var writes = _recorder.Writes - writesBefore;
        var ok = ended && text == "你好" && writes == 1;
        return (ok, $"text='{text}' writes={writes} composing={_store.IsComposing}");
    }

    private (bool Ok, string Detail) RunEscapeCancel(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s2-composing");
        _input.PressVirtualKey(VkEscape, "escape");
        var ended = WaitUntil(() => !_store.IsComposing, 4_000);
        Wait(300);
        Capture("s2-cancelled");

        var text = _store.Document.Text;
        var writes = _recorder.Writes - writesBefore;
        var ok = ended && text.Length == 0 && writes == 0 && NativeMethods.IsWindow(_window.Hwnd);
        return (ok, $"text='{text}' writes={writes} composing={_store.IsComposing} alive={NativeMethods.IsWindow(_window.Hwnd)}");
    }

    private (bool Ok, string Detail) RunEnterSelect(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s3-composing");
        _input.PressVirtualKey(VkReturn, "enter");
        var ended = WaitUntil(() => !_store.IsComposing, 4_000);
        Wait(300);
        Capture("s3-committed");

        var text = _store.Document.Text;
        var writes = _recorder.Writes - writesBefore;
        var ok = ended && writes == 1 && text.Length > 0 && !text.Contains('\n') && !text.Contains('\r');
        return (ok, $"text='{text}' writes={writes} composing={_store.IsComposing} newline={text.Contains('\n')}");
    }

    private (bool Ok, string Detail) RunScrollFollow(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s4a-composing");
        CaptureFocusSnapshotAt("scroll-before-publish");
        ProbeContextViewAt("scroll-before");
        var beforeRow = _window.TextRowScreenRect;
        var beforeGeneration = _store.LayoutGeneration;
        var physicalDelta = _window.ScrollByLogical(_options.ScrollDelta);
        _window.Invoke(() => { });
        CaptureFocusSnapshotAt("scroll-after-publish");
        Wait(500);
        Capture("s4b-small-scroll");
        ProbeContextViewAt("scroll-after");

        var afterRow = _window.TextRowScreenRect;
        var writes = _recorder.Writes - writesBefore;
        var composingDuring = _store.IsComposing;
        var moved = afterRow.Top == beforeRow.Top - physicalDelta;
        var ok = composingDuring && writes == 0 && moved && _store.LayoutGeneration > beforeGeneration;
        CleanupComposition();
        return (ok, $"geometry/composition checks only; candidate_visual=pending-not-assessed; row {beforeRow.Top}->{afterRow.Top} logical={_options.ScrollDelta} physical={physicalDelta} dpi={_window.CurrentDpi} generation={beforeGeneration}->{_store.LayoutGeneration} writes={writes} composing_during={composingDuring} composing_after_cleanup={_store.IsComposing}");
    }

    private (bool Ok, string Detail) RunMoveWindow(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s5a-composing");
        ProbeContextViewAt("move-before");
        var beforeRow = _window.TextRowScreenRect;
        _window.MoveWindowBy(0, -24);
        _window.Invoke(() => { });
        Wait(600);
        Capture("s5b-moved");
        ProbeContextViewAt("move-after");

        var afterRow = _window.TextRowScreenRect;
        var writes = _recorder.Writes - writesBefore;
        var composingDuring = _store.IsComposing;
        var ok = composingDuring && writes == 0 && afterRow.Top == beforeRow.Top - 24;
        CleanupComposition();
        return (ok, $"geometry/composition checks only; candidate_visual=pending-not-assessed; row {beforeRow.Top}->{afterRow.Top} writes={writes} composing_during={composingDuring} composing_after_cleanup={_store.IsComposing}");
    }

    private (bool Ok, string Detail) RunResize(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s6a-composing");
        var beforeClient = _window.ClientScreenRect;
        _window.ResizeWindow(920, 540);
        _window.Invoke(() => { });
        Wait(600);
        Capture("s6b-resized");

        var afterClient = _window.ClientScreenRect;
        var writes = _recorder.Writes - writesBefore;
        var composingDuring = _store.IsComposing;
        var ok = composingDuring && writes == 0
            && (afterClient.Width != beforeClient.Width || afterClient.Height != beforeClient.Height);
        CleanupComposition();
        return (ok, $"geometry/composition checks only; candidate_visual=pending-not-assessed; client {beforeClient.Width}x{beforeClient.Height}->{afterClient.Width}x{afterClient.Height} writes={writes} composing_during={composingDuring} composing_after_cleanup={_store.IsComposing}");
    }

    private (bool Ok, string Detail) RunClipOut(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s7a-composing");
        ProbeContextViewAt("clip-before");
        var client = _window.ClientScreenRect;
        var row = _window.TextRowScreenRect;
        var delta = Math.Max(1, row.Bottom - client.Top + 40);
        _window.ScrollBy(delta);
        _window.Invoke(() => { });
        Wait(600);
        Capture("s7b-clipped-out");
        ProbeContextViewAt("clip-after");

        var clippedRow = _window.TextRowScreenRect;
        var fullyClipped = clippedRow.Bottom <= client.Top;
        var writesAfterClip = _recorder.Writes - writesBefore;

        var suppressDetail = "(not requested)";
        if (_options.SuppressCandidateUi && _session.UiElements is { } observer)
        {
            var results = _window.Invoke(() => observer.TryShowCandidates(false));
            suppressDetail = DescribeShowResults(results);
            Wait(500);
            Capture("s7c-candidate-show-false");
        }

        var composingWhileClipped = _store.IsComposing;
        _window.ScrollBy(-delta);
        _window.Invoke(() => { });
        Wait(600);
        Capture("s7d-restored");

        if (_options.SuppressCandidateUi && _session.UiElements is { } restoreObserver)
        {
            var results = _window.Invoke(() => restoreObserver.TryShowCandidates(true));
            _trace("scenario", "clip-out-restore-ui", new Dictionary<string, object?>
            {
                ["results"] = DescribeShowResults(results),
            });
        }

        var composingRestored = _store.IsComposing;
        var ok = composingWhileClipped && fullyClipped && writesAfterClip == 0
            && composingRestored && _store.Document.Text.Length > 0;
        CleanupComposition();
        return (ok, $"geometry/composition checks only; candidate_visual=pending-not-assessed; row_bottom={clippedRow.Bottom} client_top={client.Top} fully_clipped={fullyClipped} writes={writesAfterClip} composing_clipped={composingWhileClipped} composing_restored={composingRestored} composing_after_cleanup={_store.IsComposing} suppress={suppressDetail}");
    }

    // -------------------------------------------------------------------
    // R29B opt-in：真实 input child 可见几何场景（共享执行器 + 本类宿主包装）。
    // -------------------------------------------------------------------

    private (bool Ok, string Detail) RunChildScrollFollow(int writesBefore)
        => RunChildGeometry(ChildSurfaceScenario.ScrollFollow, writesBefore,
            _options.ScrollDelta);

    private (bool Ok, string Detail) RunChildClipOut(int writesBefore)
        => RunChildGeometry(ChildSurfaceScenario.ClipOut, writesBefore, 0);

    private (bool Ok, string Detail) RunChildMoveWindow(int writesBefore)
        => RunChildGeometry(ChildSurfaceScenario.MoveWindow, writesBefore, 0);

    /// <summary>
    /// opt-in 几何场景入口：prepare（nihao 组合 + safepoint 身份核验）→ 共享执行器
    /// （基线/动作/恢复三段 safepoint+截图+精确回滚）→ 清理。守卫（BeforeSendGuard）
    /// 贯穿 prepare 与 cleanup Esc；executor 失败如实计入 failed，绝不宣称候选通过。
    /// </summary>
    private (bool Ok, string Detail) RunChildGeometry(ChildSurfaceScenario scenario,
        int writesBefore, int logicalDelta)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        // 组合已 settle：执行器基线快照（含 text/span/composing/profile + 连续性计数 + writes=0）。
        var host = new ChildSurfaceScenarioHost(this, writesBefore);
        var executor = new ChildSurfaceGeometryExecutor(host,
            baseline => SetLifecycleBaseline(writesBefore, baseline),
            _options.ChildHideWhenClipped, _options.ChildSuspendTsfFocusWhenClipped);
        var ok = executor.Run(scenario, logicalDelta);

        // R37 opt-in：执行器全部恢复证据成立之后、清理组合之前，证明<b>恢复后的当前候选</b>
        // 真的能提交。只在执行器成功时进行——执行器失败时组合/几何事实已不可信，
        // 绝不在此基础上再发一次真实按键。默认 off 完全跳过（R36 证明路径不变）。
        var restoredCommit = "(not-requested)";
        if (_options.VerifyRestoredCommit && ok && executor.Stop == ChildSurfaceStop.None)
        {
            var committed = TryVerifyRestoredCommit(executor.Baseline, writesBefore,
                out var commitDetail);
            restoredCommit = commitDetail;
            if (!committed)
            {
                ok = false;
            }
        }

        // 清理：守卫仍挂在 _input 上；只在测得间隔之后由 EnsureNoComposition 发 Esc。
        CleanupComposition();

        var stage = executor.StoppedAtStage;
        var stop = executor.Stop;
        var stopDetail = executor.StopDetail;
        var summary = $"candidate_visual=pending-not-assessed; scenario={scenario} "
            + $"stop={stop} stage={stage} detail={stopDetail} "
            + $"baseline={(executor.Baseline?.Describe() ?? "(none)")} "
            + $"restored_commit={restoredCommit} "
            + $"composing_after_cleanup={_store.IsComposing}";
        return (ok, summary);
    }

    // -------------------------------------------------------------------
    // R37：恢复后提交证明（默认 off；唯一组合 = host-drawn-candidates + child-input-surface
    // + clip-out + STA）。
    //
    // 只在执行器全部恢复证据成立后运行；不夺焦、不重打、不取消、不改任何生产代码路径。
    // 唯一发出的键是真实 Space（经既有 BeforeSendGuard，down/up 各自独立过门）。
    // 判定由 <see cref="RestoredCommitGate"/> 的共享谓词完成——生产与单元测试同一实现。
    // -------------------------------------------------------------------

    /// <summary>
    /// 提交前的单个 UI 安全点读数：严格安全点（含 profile COM 查询）→ 与执行器基线的
    /// 完整不变量 → store 快照 → presenter 当前快照（id/代数/页内选中文本）→ overlay 真实
    /// 可见性。全部实际读取；任一查询失败如实反映在返回事实中（绝不用零值冒充成功）。
    /// </summary>
    private RestoredCommitPreFacts ReadRestoredCommitPreFacts(LifecycleSnapshot? baseline,
        int writesBefore, string stage)
    {
        return _window.Invoke(() =>
        {
            var snapshot = ProbeLifecycleSafePoint(writesBefore, stage, false,
                out var ok, out var detail);
            var violation = baseline is { } b
                ? snapshot.DescribeInvariantViolation(b)
                : "executor baseline missing";

            var presenter = _session.UiElements?.HostDrawnPresenter;
            var current = presenter?.Current;
            var selectionOnPage = current?.SelectionOnPage ?? -1;
            string? selectedText = null;
            if (current is { Valid: true } valid
                && selectionOnPage >= 0 && selectionOnPage < valid.Entries.Count)
            {
                selectedText = valid.Entries[selectionOnPage].Text;
            }

            var inspection = _window.InspectCandidateOverlay(stage + ":overlay");

            return new RestoredCommitPreFacts(
                ok, detail, violation,
                snapshot.Text, snapshot.SpanStart, snapshot.SpanEnd, snapshot.Writes,
                // 绝对条目数在同一安全点内读取：提交后据此取增量，绝不跨派发拼接。
                _recorder.Writes,
                snapshot.Composing, snapshot.Profile,
                snapshot.CompositionBeginCount, snapshot.CompositionEndCount,
                snapshot.CompositionSessionId, snapshot.InputFocusChangeCount,
                presenter is not null,
                current is { Valid: true },
                current?.ElementId ?? 0,
                current?.Generation ?? 0,
                current?.Entries.Count ?? 0,
                selectedText,
                selectionOnPage,
                inspection.Ok,
                inspection.Visible,
                inspection.ElementId,
                inspection.Generation,
                inspection.Failure);
        });
    }

    /// <summary>
    /// 提交后的单个 UI 安全点读数：严格安全点（profile 仍必须可核验）→ store 快照 →
    /// 绑定条目增量（实际记录，含文本/来源）→ presenter 是否已清空 → overlay 真实可见性。
    /// </summary>
    private RestoredCommitPostFacts ReadRestoredCommitPostFacts(int writesBefore,
        int preAbsoluteWrites, bool compositionEnded, int? writesAtEndOfWait, string stage)
    {
        return _window.Invoke(() =>
        {
            var snapshot = ProbeLifecycleSafePoint(writesBefore, stage, false,
                out var ok, out var detail);

            var entries = _recorder.Entries;
            var newWrites = entries.Count > preAbsoluteWrites
                ? entries.Skip(preAbsoluteWrites).ToArray()
                : Array.Empty<BindingWrite>();

            var presenter = _session.UiElements?.HostDrawnPresenter;
            var current = presenter?.Current;
            var inspection = _window.InspectCandidateOverlay(stage + ":overlay");

            return new RestoredCommitPostFacts(
                ok, detail,
                snapshot.Text,
                snapshot.Writes,
                snapshot.Composing,
                snapshot.CompositionBeginCount,
                snapshot.CompositionEndCount,
                snapshot.CompositionSessionId,
                snapshot.InputFocusChangeCount,
                snapshot.Profile,
                _recorder.Writes - writesBefore,
                newWrites,
                compositionEnded,
                current is not { Valid: true },
                inspection.Ok,
                inspection.Visible,
                writesAtEndOfWait);
        });
    }

    /// <summary>
    /// R37 提交证明主体。返回 false 时 <paramref name="detail"/> 携带可读失败原因，
    /// 调用方据此把场景判失败（绝不把诊断属性当通过）。成功路径同时记录
    /// begin/end/verdict 结构化日志与可选 post-commit root 证据图。
    /// </summary>
    private bool TryVerifyRestoredCommit(LifecycleSnapshot? baseline, int writesBefore,
        out string detail)
    {
        var expectedText = RestoredCommitExpectation.ExpectedText;

        // ---- 1) 提交前严格安全点 + 同一 presenter 当前快照 + 真实 overlay 可见性 ----
        var pre = ReadRestoredCommitPreFacts(baseline, writesBefore,
            "restored-commit-pre");
        // 提交前绝对条目数取自同一个安全点读数（见 RestoredCommitPreFacts.AbsoluteWrites）。
        var preAbsoluteWrites = pre.AbsoluteWrites;

        // 原始零写入区间：执行器基线（writes=0）到提交前一刻仍未写入——这段区间在
        // 发键之前如实落日志，之后的任何写入都归因于本次提交。
        _trace("restored-commit", "restored-commit-zero-write-interval",
            new Dictionary<string, object?>
            {
                ["scenario"] = _currentScenario,
                ["interval"] = "baseline->pre-commit",
                ["writes"] = pre.Writes,
                ["baseline_writes"] = baseline?.Writes,
                ["text"] = ImeFormat.EscapeUtf16(pre.Text),
                ["span"] = $"{pre.SpanStart}..{pre.SpanEnd}",
                ["composing"] = pre.Composing,
                ["begin"] = pre.BeginCount,
                ["end"] = pre.EndCount,
                ["session"] = pre.SessionId,
                ["focus_events"] = pre.FocusEvents,
            });

        _trace("restored-commit", "restored-commit-begin", new Dictionary<string, object?>
        {
            ["scenario"] = _currentScenario,
            ["expected_text"] = expectedText,
            ["actual_text"] = pre.Text,
            ["actual_span"] = $"{pre.SpanStart}..{pre.SpanEnd}",
            ["selected_text"] = pre.SelectedText ?? "(none)",
            ["expected_selected_text"] = expectedText,
            ["selection_on_page"] = pre.SelectionOnPage,
            ["element_id"] = pre.ElementId,
            ["generation"] = pre.Generation,
            ["entries"] = pre.EntryCount,
            ["presenter_available"] = pre.PresenterAvailable,
            ["presenter_valid"] = pre.PresenterValid,
            ["overlay_ok"] = pre.OverlayInspectOk,
            ["overlay_visible"] = pre.OverlayVisible,
            ["overlay_failure"] = pre.OverlayFailure ?? "(none)",
            ["writes"] = pre.Writes,
            ["binding_entries"] = preAbsoluteWrites,
            ["expected_source"] = RestoredCommitExpectation.ExpectedSource,
            ["begin"] = pre.BeginCount,
            ["end"] = pre.EndCount,
            ["session"] = pre.SessionId,
            ["focus_events"] = pre.FocusEvents,
            ["profile"] = pre.Profile,
            ["safe_point"] = pre.SafePointDetail,
            ["invariant_violation"] = pre.InvariantViolation ?? "(none)",
            ["native_foreground"] = DescribeForeground(),
            ["native_focus"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
            ["root_hwnd"] = $"0x{_window.Hwnd.ToInt64():X}",
            ["input_hwnd"] = $"0x{_window.InputHwnd.ToInt64():X}",
            ["facts"] = pre.Describe(),
        });

        if (RestoredCommitGate.EvaluatePre(pre) is { } preFailure)
        {
            detail = $"restored-commit=pre-gate-failed: {preFailure}";
            EmitRestoredCommitVerdict(false, detail, pre, null, "(no-key-sent)");
            return false;
        }

        // ---- 2) 一次真实 Space（既有 BeforeSendGuard 对 down/up 各自生效）----
        var sent = _input.PressVirtualKey(VkSpace, RestoredCommitExpectation.KeyLabel);
        _trace("restored-commit", "restored-commit-key", new Dictionary<string, object?>
        {
            ["scenario"] = _currentScenario,
            ["virtual_key"] = $"0x{VkSpace:X2}",
            ["label"] = RestoredCommitExpectation.KeyLabel,
            ["sent"] = sent,
            ["guard"] = "BeforeSendGuard evaluated per down/up",
            ["retyped"] = false,
            ["forced_focus"] = false,
            ["cancelled"] = false,
        });

        if (!sent)
        {
            // 守卫拒绝/前台丢失：绝不重试、绝不夺焦——如实失败。
            detail = "restored-commit=key-send-refused "
                + "(BeforeSendGuard or own-foreground check refused the Space down/up)";
            EmitRestoredCommitVerdict(false, detail, pre, null, "refused");
            return false;
        }

        // ---- 3) 有界等待组合真实结束（3s）→ 400ms 稳定窗口 ----
        var ended = WaitUntil(() => !_store.IsComposing, RestoredCommitExpectation.CommitWaitMs);
        var writesAtEndOfWait = _recorder.Writes - writesBefore;
        Wait(RestoredCommitExpectation.StableWindowMs);

        // ---- 4) 提交后严格安全点 + 实际记录条目 + presenter/overlay 真实状态 ----
        var post = ReadRestoredCommitPostFacts(writesBefore, preAbsoluteWrites, ended,
            writesAtEndOfWait, "restored-commit-post");

        if (RestoredCommitGate.EvaluatePost(pre, post) is { } postFailure)
        {
            detail = $"restored-commit=post-gate-failed: {postFailure}";
            EmitRestoredCommitVerdict(false, detail, pre, post, sent ? "sent" : "not-sent");
            return false;
        }

        // ---- 5) 可选 post-commit root 证据图（守卫前后各一次 + 已提交状态保持）----
        var image = "(skipped)";
        var imageOk = CaptureRestoredCommitImage(writesBefore, preAbsoluteWrites,
            writesAtEndOfWait, out image);

        // 门禁已确认恰好一条记录：此处只做如实取值（缺失时按 none 描述，绝不伪造）。
        var committedEntry = post.NewWrites is { Count: > 0 } entries ? entries[0] : default;
        var entryText = post.NewWrites is { Count: > 0 } ? committedEntry.Text : "(none)";
        var entrySource = post.NewWrites is { Count: > 0 } ? committedEntry.Source : "(none)";

        var verdictDetail = $"restored-commit=committed text='{post.Text}' "
            + $"writes_delta={post.WriteDelta} entry='{entryText}' source='{entrySource}' "
            + $"end={post.EndCount} begin={post.BeginCount} session={post.SessionId} "
            + $"presenter_cleared={post.PresenterCleared} overlay_visible={post.OverlayVisible} "
            + $"image_ok={imageOk}";

        EmitRestoredCommitVerdict(true, verdictDetail, pre, post, "sent");
        detail = verdictDetail + $" image={image}";
        return true;
    }

    /// <summary>
    /// R37 可选 post-commit root 证据图。门禁是<b>已提交状态保持</b>（文本精确、写入增量 1、
    /// 不在组合中、presenter 已清空、overlay 真实不可见、严格安全点成立），
    /// <b>刻意不复用</b>组合中的旧基线门禁（那要求 Composing==true，提交后必然失败），
    /// 也不放宽任何既有门禁——本图只作为可选补充证据，失败不影响已成立的提交判定。
    /// </summary>
    private bool CaptureRestoredCommitImage(int writesBefore, int preAbsoluteWrites,
        int? writesAtEndOfWait, out string detail)
    {
        var path = Path.Combine(_options.OutDir,
            RestoredCommitExpectation.PostCommitImageName + ".png");
        var ok = false;
        var reason = "ok";

        _window.Invoke(() =>
        {
            // 前置：严格安全点 + 已提交状态保持（不拿组合中基线套提交后状态）。
            var before = ReadRestoredCommitPostFacts(writesBefore, preAbsoluteWrites, true,
                writesAtEndOfWait, "restored-commit-image-pre");
            var beforeFailure = RestoredCommitGate.EvaluateCommittedStatePreserved(before);
            if (beforeFailure is not null)
            {
                reason = "pre: " + beforeFailure;
                _trace("restored-commit", "restored-commit-image", new Dictionary<string, object?>
                {
                    ["path"] = "(not-taken)",
                    ["ok"] = false,
                    ["reason"] = reason,
                    ["before"] = before.Describe(),
                });
                return;
            }

            var captured = _window.Capture(path);
            if (captured is null)
            {
                reason = "capture failed";
                _trace("restored-commit", "restored-commit-image", new Dictionary<string, object?>
                {
                    ["path"] = "(failed)",
                    ["ok"] = false,
                    ["reason"] = reason,
                    ["before"] = before.Describe(),
                });
                return;
            }

            // 后置：抓图之后再读一次严格安全点与已提交状态——期间变化即本图无效。
            var after = ReadRestoredCommitPostFacts(writesBefore, preAbsoluteWrites, true,
                writesAtEndOfWait, "restored-commit-image-post");
            var afterFailure = RestoredCommitGate.EvaluateCommittedStatePreserved(after);
            ok = afterFailure is null;
            reason = afterFailure is null ? "ok" : "post: " + afterFailure;

            _trace("restored-commit", "restored-commit-image", new Dictionary<string, object?>
            {
                ["path"] = captured.Value.Path,
                ["x"] = captured.Value.X,
                ["y"] = captured.Value.Y,
                ["width"] = captured.Value.Width,
                ["height"] = captured.Value.Height,
                ["ok"] = ok,
                ["reason"] = reason,
                ["before"] = before.Describe(),
                ["after"] = after.Describe(),
            });
        });

        detail = ok ? path : $"(not-valid: {reason})";
        return ok;
    }

    /// <summary>提交判定的结构化结论（成功/失败都记；含前后真实事实与原因）。</summary>
    private void EmitRestoredCommitVerdict(bool ok, string reason,
        in RestoredCommitPreFacts pre, RestoredCommitPostFacts? post, string keyState)
    {
        _trace("restored-commit", "restored-commit-end", new Dictionary<string, object?>
        {
            ["scenario"] = _currentScenario,
            ["verdict"] = ok ? "committed" : "failed",
            ["ok"] = ok,
            ["reason"] = reason,
            ["expected_text"] = RestoredCommitExpectation.ExpectedText,
            ["expected_writes"] = RestoredCommitExpectation.ExpectedWrites,
            ["expected_source"] = RestoredCommitExpectation.ExpectedSource,
            ["key"] = keyState,
            ["pre"] = pre.Describe(),
            ["post"] = post?.Describe() ?? "(not-read)",
            ["post_entries"] = post?.DescribeEntries() ?? "(none)",
            ["post_text"] = post?.Text ?? "(not-read)",
            ["post_writes_delta"] = post?.WriteDelta,
            ["post_end"] = post?.EndCount,
            ["post_begin"] = post?.BeginCount,
            ["post_session"] = post?.SessionId,
            ["post_profile"] = post?.Profile ?? "(not-read)",
            ["post_presenter_cleared"] = post?.PresenterCleared,
            ["post_overlay_visible"] = post?.OverlayVisible,
            ["native_foreground"] = DescribeForeground(),
            ["native_focus"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
            ["diagnostic_only"] = false,
        });
    }

    /// <summary>
    /// R31 返修：每图 TSF 焦点阶段读取器的<b>唯一选择点</b>——仅当
    /// <c>--child-suspend-tsf-focus-when-clipped</c> 启用时才把当前宿主同一实例的
    /// 实际阶段读取器注入每图管线；默认 off 返回 null，R29/R30 默认路径不产生任何
    /// 新增 TSF 查询。测试经本静态入口验证"off→null、on→同一个回调"，
    /// 绝不复制一份平行选择逻辑。
    /// </summary>
    internal static Func<TsfFocusPhaseResult>? SelectCapturePhaseReader(
        bool suspendTsfFocusWhenClipped, Func<TsfFocusPhaseResult> reader)
        => suspendTsfFocusWhenClipped ? reader : null;

    /// <summary>
    /// opt-in 几何执行器的真实宿主：把 <see cref="IChildSurfaceGeometryHost"/> 桥到
    /// 本 runner 的 safepoint/capture 与 TsfHostWindow 的真实几何 API。
    /// 全部几何/TSF 查询都在 <see cref="InvokeOnUiThread"/> 派发的 UI body 内完成，
    /// 测试替身以同一路径驱动执行器——共享控制流，无 mock-of-uncalled。
    /// </summary>
    private sealed class ChildSurfaceScenarioHost : IChildSurfaceGeometryHost
    {
        private readonly ScenarioRunner _runner;
        private readonly int _writesBefore;

        internal ChildSurfaceScenarioHost(ScenarioRunner runner, int writesBefore)
        {
            _runner = runner;
            _writesBefore = writesBefore;
        }

        public bool InvokeOnUiThread(Func<bool> action)
            => _runner._window.Invoke(action);

        public SafePointProbe Probe(string stage)
        {
            SafePointProbe result = default;
            _runner._window.Invoke(() =>
            {
                var snapshot = _runner.ProbeLifecycleSafePoint(_writesBefore, stage,
                    false, out var ok, out var detail);
                result = new SafePointProbe(ok, detail, snapshot.Text,
                    snapshot.SpanStart, snapshot.SpanEnd, snapshot.Writes,
                    snapshot.Composing, snapshot.Profile,
                    snapshot.CompositionBeginCount, snapshot.CompositionEndCount,
                    snapshot.CompositionSessionId, snapshot.InputFocusChangeCount);
            });
            return result;
        }

        public void Record(string kind, IDictionary<string, object?> fields)
            => _runner._trace("scenario", kind, fields);

        /// <summary>
        /// R31 复审返修：把<b>本宿主同一实例</b>的实际 TSF 焦点阶段读取器显式
        /// 传入每张抓图——共享 <see cref="TsfDocumentFocus.ReadPhase"/> 与同一组
        /// 真实委托，绝不新建第二个默认 OWN 阶段；root/monitor 前后各实际读回
        /// 并在阶段 COM 查询后复核组合/原生安全点。
        /// 读取器<b>只</b>在 <c>ChildSuspendTsfFocusWhenClipped</c> 启用时经
        /// <see cref="SelectCapturePhaseReader"/> 接线；默认 off 传 null，
        /// 每图管线保持仅组合/原生复核，不新增任何 TSF 查询。
        /// </summary>
        public CaptureVerdict Capture(string name)
            => _runner.CaptureChildSurface(name, SelectCapturePhaseReader(
                _runner._options.ChildSuspendTsfFocusWhenClipped, ReadTsfFocusPhase));

        public void Wait(int milliseconds) => Thread.Sleep(Math.Max(1, milliseconds));

        public int GetScrollX() => _runner._window.Invoke(() => _runner._window.ScrollOffsetX);

        public int GetScrollY() => _runner._window.Invoke(() => _runner._window.ScrollOffsetY);

        public void SetScrollOffset(int x, int y)
            => _runner._window.Invoke(() => { _ = _runner._window.SetScroll(x, y); });

        public int ScrollByLogical(int logicalDelta)
            => _runner._window.Invoke(() => _runner._window.ScrollByLogical(logicalDelta));

        public void ScrollByPhysical(int deltaY)
            => _runner._window.Invoke(() => { _ = _runner._window.ScrollBy(deltaY); });

        public void MoveRootBy(int dx, int dy)
            => _runner._window.Invoke(() => { _ = _runner._window.MoveWindowBy(dx, dy); });

        /// <summary>
        /// 把 root 顶层窗口按保存的<b>绝对原点</b>恢复（NOACTIVATE|NOSIZE|NOZORDER，
        /// 不夺回焦点/前台）；恢复前复核 root/input HWND 仍属本进程本 UI 线程，
        /// 不操作外来或已回收句柄。
        /// </summary>
        public void SetRootWindowOrigin(int x, int y)
            => _runner._window.Invoke(() => { _ = _runner._window.SetRootWindowOrigin(x, y); });

        public TsfRect GetTextRowScreenRect()
            => _runner._window.Invoke(() => _runner._window.TextRowScreenRect);

        public TsfRect GetInputClientScreenRect()
            => _runner._window.Invoke(() => _runner._window.InputSurfaceScreenRect);

        public TsfRect GetViewportClientScreenRect()
            => _runner._window.Invoke(() => _runner._window.ViewportClientScreenRect);

        /// <summary>root 顶层窗口的真实屏幕矩形（move-window 绝对恢复用）。</summary>
        public TsfRect GetRootWindowScreenRect()
            => _runner._window.Invoke(() => _runner._window.RootWindowScreenRect);

        public int GetScreenExt(out TsfRect rect)
        {
            var result = default(TsfRect);
            var hr = _runner._window.Invoke(() =>
                _runner._store.GetScreenExt(_runner._store.ViewCookie, out result));
            rect = result;
            return hr;
        }

        public int GetTextExt(int acpStart, int acpEnd, out TsfRect rect, out int clipped)
        {
            var resultRect = default(TsfRect);
            var resultClipped = 0;
            var hr = _runner._window.Invoke(() =>
            {
                // 真实读锁内的只读查询（与 ContextViewProbe 同源的持锁前提）：
                // 模拟 TSF 在只读编辑会话持锁时查询 store 几何。
                return _runner.InvokeReadLockedTextExt(acpStart, acpEnd,
                    out resultRect, out resultClipped);
            });
            rect = resultRect;
            clipped = resultClipped;
            return hr;
        }

        // R30：真实宿主实现自有 input child 的可见性操作（执行器在受管 UI body 内调用；
        // 方法内部再 Invoke 到 UI 线程是幂等同一线程直执）。showOnlyIfWasVisible 由
        // 执行器从 hide 操作读回的 WasVisible 透传——原本就不可见时只读回、不写。
        public bool SupportsVisibilityOperation => true;

        public ChildSurfaceVisibilityResult VisibilityOperation(bool show, bool showOnlyIfWasVisible)
            => _runner._window.SetInputSurfaceVisibility(show, showOnlyIfWasVisible);

        /// <summary>
        /// R30：执行器在受管 UI body 内（已是 owning UI 线程）调用——本读数只经归属
        /// 门禁核对后读真实 <c>IsWindowVisible</c>，不写、不夺焦。归属校验失败如实
        /// 回 false，由执行器在 hide 前按"读取失败"对待（hide 前的 op 复核会再判）。
        /// </summary>
        public bool ReadInputVisibility()
            => _runner._window.Invoke(_runner._window.ReadInputSurfaceVisibility);

        // ---- R31：真实 ITfThreadMgr 文档焦点操作（全部在 UI 线程内）--------------
        //
        // 归属门禁与几何操作同源：root+input HWND 仍属本进程本 UI 线程才允许触碰
        // TSF 焦点；门禁失败如实回失败结果，绝不调用 COM。所有 GetFocus/SetFocus/
        // GetTop 都经共享 <see cref="TsfDocumentFocus"/> 操作核——读回身份是真源。
        // 不做 HWND SetFocus/AssociateFocus/Pop/Push，不结束组合，不重输文本。
        //
        // R31 复审：共享可注入门禁 = 前台==root + Win32 焦点==input + HWND 归属
        // + store 无锁 + 无原生回调重入（LayoutNotifyDepth==0）。每次 COM 查询/
        // 写入前后各复核一次；受控阶段由共享 <see cref="TsfFocusStage"/> 跟踪，
        // 期望只在已验证的 suspend/restore 成功后迁移。

        /// <summary>当前受控 TSF 焦点阶段（与执行器/测试替身共用同一对象语义；
        /// 期望只在已验证 suspend/restore 成功后迁移）。</summary>
        private readonly TsfFocusStage _tsfFocusStage = new();

        /// <summary>真实 GetFocus 委托：ITfThreadMgr.GetFocus；取得的 doc RCW 归调用方
        /// 成对释放（Marshal.ReleaseComObject）。</summary>
        private (int Hr, object? Doc) TsfGetFocus()
        {
            var threadMgr = _runner._session.ThreadManager;
            if (threadMgr is null)
            {
                return (TsfHResult.E_FAIL, null);
            }

            var hr = threadMgr.GetFocus(out var doc);
            return (hr, doc);
        }

        /// <summary>真实 SetFocus 委托：只传 doc 或 null；绝不传 HWND。</summary>
        private int TsfSetFocus(object? doc)
        {
            var threadMgr = _runner._session.ThreadManager;
            if (threadMgr is null)
            {
                return TsfHResult.E_FAIL;
            }

            // TsfFocusHandle.Doc 是不透明 object；真实路径总是 ITfDocumentMgr RCW。
            return threadMgr.SetFocus(doc as ITfDocumentMgr);
        }

        /// <summary>真实 GetTop 委托。</summary>
        private (int Hr, object? Top) TsfGetTop(object doc)
        {
            if (doc is not ITfDocumentMgr mgr)
            {
                return (TsfHResult.E_FAIL, null);
            }

            var hr = mgr.GetTop(out var top);
            return (hr, top);
        }

        /// <summary>成对释放一个本层取得的 RCW；只释放我们 Acquire/读回新建的那次
        /// 引用（Marshal.ReleaseComObject 减一次 RCW 引用计数）——会话长期持有的
        /// ThreadMgr/DocumentMgr/Context RCW 绝不在此释放。</summary>
        private static void TsfRelease(object comObject)
        {
            if (comObject is null)
            {
                return;
            }

            Marshal.ReleaseComObject(comObject);
        }

        /// <summary>归属门禁：root+input HWND 存活且属本进程本 UI 线程。</summary>
        private bool OwnsChildSurfaceOnUiThread()
        {
            var root = _runner._window.Hwnd;
            var input = _runner._window.InputHwnd;
            var ownTid = NativeMethods.CurrentWin32ThreadId();
            var ownPid = Environment.ProcessId;
            return root != IntPtr.Zero && input != IntPtr.Zero
                && NativeMethods.IsWindow(root) && NativeMethods.IsWindow(input)
                && NativeMethods.GetWindowThreadProcessId(root, out var rootPid) == ownTid
                && NativeMethods.GetWindowThreadProcessId(input, out var inputPid) == ownTid
                && rootPid == ownPid && inputPid == ownPid;
        }

        /// <summary>
        /// R31 复审：共享可注入门禁的<b>真实实现</b>——前台仍在自有 root、Win32
        /// 焦点仍在自有 input、root+input 仍属本进程本 UI 线程、store 无锁、
        /// 无原生回调重入（LayoutNotifyDepth==0）。每一次 COM 查询/写入前后都由
        /// <see cref="TsfDocumentFocus"/> 经此复核；返回 null 允许，非 null 为
        /// 拒绝原因（绝不写、绝不覆盖外部焦点）。
        /// </summary>
        private string? TsfFocusGuard(string stage)
        {
            // 门禁只在 UI 线程内评估（InvokeOnUiThread 已派发）。
            var root = _runner._window.Hwnd;
            var input = _runner._window.InputHwnd;
            var ownTid = NativeMethods.CurrentWin32ThreadId();
            var ownPid = Environment.ProcessId;

            if (root == IntPtr.Zero || input == IntPtr.Zero
                || !NativeMethods.IsWindow(root) || !NativeMethods.IsWindow(input))
            {
                return "own hwnd lost";
            }

            if (NativeMethods.GetWindowThreadProcessId(root, out var rootPid) != ownTid
                || NativeMethods.GetWindowThreadProcessId(input, out var inputPid) != ownTid
                || rootPid != ownPid || inputPid != ownPid)
            {
                return "hwnd ownership changed";
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != root)
            {
                return $"foreground is not own root (0x{foreground.ToInt64():X})";
            }

            var focus = NativeMethods.GetFocus();
            if (focus != input)
            {
                return $"win32 focus is not own input (0x{focus.ToInt64():X})";
            }

            var store = _runner._store.SnapshotForLifecycle();
            if (store.LockType != 0)
            {
                return $"store lock held (lock_type=0x{store.LockType:X})";
            }

            var layoutDepth = _runner._store.LayoutNotifyDepth;
            if (layoutDepth != 0)
            {
                return $"native layout callback reentry (depth={layoutDepth})";
            }

            _ = stage; // stage 已随拒绝原因经调用方记录。
            return null;
        }

        public bool SupportsTsfFocusOperation => true;

        /// <summary>受控 TSF 焦点阶段（执行器经此迁移期望：OWN→NULL→SAME_OWN）。</summary>
        public TsfFocusStage TsfFocusStageState => _tsfFocusStage;

        /// <summary>R31 复审：实际读回真实 TSF 焦点并按当前受控期望阶段判定。
        /// 共享 <see cref="TsfDocumentFocus.ReadPhase"/> 与同一组真实委托——
        /// 测试替身注入的是同一抽象，不是平行实现。</summary>
        public TsfFocusPhaseResult ReadTsfFocusPhase()
        {
            TsfFocusPhaseResult result = default;
            _runner._window.Invoke(() =>
            {
                result = TsfDocumentFocus.ReadPhase(TsfGetFocus,
                    _runner._session.DocumentManager, _runner._session.Context,
                    TsfGetTop, TsfRelease, _tsfFocusStage.Saved,
                    _tsfFocusStage.Phase, TsfFocusGuard, TsfFocusGuard);
            });
            return result;
        }

        /// <summary>R31 复审：清理动作前的最后一刻原生归属门禁——失去前台/
        /// 焦点/归属时如实拒绝（执行器据此跳过清理写，绝不夺焦/覆盖）。</summary>
        public string? CheckTsfFocusCleanupAllowed()
        {
            string? refuse = null;
            _runner._window.Invoke(() =>
            {
                refuse = TsfFocusGuard("cleanup");
            });
            return refuse;
        }

        public TsfFocusAcquireResult AcquireTsfFocus()
        {
            TsfFocusAcquireResult result = default;
            _runner._window.Invoke(() =>
            {
                if (!OwnsChildSurfaceOnUiThread())
                {
                    result = new TsfFocusAcquireResult(false, null,
                        "child-surface ownership check failed");
                    return;
                }

                result = TsfDocumentFocus.Acquire(TsfGetFocus,
                    _runner._session.DocumentManager, _runner._session.Context,
                    TsfGetTop, TsfRelease, TsfFocusGuard, TsfFocusGuard);
            });
            return result;
        }

        public TsfFocusOpResult SuspendTsfFocus()
        {
            TsfFocusOpResult result = default;
            _runner._window.Invoke(() =>
            {
                if (!OwnsChildSurfaceOnUiThread())
                {
                    result = new TsfFocusOpResult(false, false,
                        "child-surface ownership check failed");
                    return;
                }

                result = TsfDocumentFocus.Suspend(TsfGetFocus, TsfSetFocus,
                    _runner._session.DocumentManager, _runner._session.Context,
                    TsfGetTop, TsfRelease, TsfFocusGuard, TsfFocusGuard);
            });
            return result;
        }

        public TsfFocusOpResult RestoreTsfFocus(TsfFocusHandle saved)
        {
            TsfFocusOpResult result = default;
            _runner._window.Invoke(() =>
            {
                if (!OwnsChildSurfaceOnUiThread())
                {
                    result = new TsfFocusOpResult(false, false,
                        "child-surface ownership check failed");
                    return;
                }

                result = TsfDocumentFocus.Restore(saved, TsfGetFocus, TsfSetFocus,
                    TsfGetTop, TsfRelease, TsfFocusGuard, TsfFocusGuard);
            });
            return result;
        }

        public TsfFocusReleaseResult ReleaseTsfFocus(TsfFocusHandle? handle)
        {
            TsfFocusReleaseResult result = TsfFocusReleaseResult.Success;
            _runner._window.Invoke(() =>
            {
                result = TsfDocumentFocus.ReleaseHandle(handle, TsfRelease);
            });
            return result;
        }
    }

    /// <summary>R27：原生 UIElement 生命周期对照（单变量）。
    /// <para>
    /// 官方契约：`BeginUIElement` 返回 FALSE 后 TIP 应继续 `UpdateUIElement`，
    /// 且应用可对同一存活元素调用 `ITfUIElement::Show` 显示 TIP 原生 UI。
    /// 本场景只做这一件事：先抑制候选（仅当前候选元素），等待存活元素，
    /// 再对**同一存活候选**依次 Show(TRUE)/Show(FALSE)/Show(TRUE) 并截图。
    /// </para>
    /// <para>
    /// 不做：自绘候选、读写系统候选 HWND、改 profile/focus、取消/提交/重打组合、
    /// 追加滚动或子窗口移动。Show 成功**不等于**图像已显示，视觉一律 pending。
    /// </para>
    /// </summary>
    /// <summary>
    /// R27：原生 UIElement 生命周期对照（单变量），实际执行委托给
    /// <see cref="NativeUiLifecycleExecutor"/>——测试也使用同一个执行器，不另造流程。
    /// <para>
    /// 所有安全点检查、快照比对与 Show 调用都在 UI 线程内完成；任何偏差立即停止，
    /// 不夺焦点、不做后续截图。
    /// </para>
    /// </summary>
    private (bool Ok, string Detail) RunNativeUiLifecycle(int writesBefore)
    {
        var observer = _session.UiElements;
        if (observer is null)
        {
            return (false, "ITfUIElementMgr/ITfUIElementSink unavailable");
        }

        var previousSuppress = observer.SuppressCandidates;
        var stages = new List<string>();
        var host = new ScenarioLifecycleHost(this, observer, writesBefore);
        var executor = new NativeUiLifecycleExecutor(host, snapshot =>
        {
            // 基线一旦确定就登记给截图复核使用（截图前/后都要与同一基线比较）。
            SetLifecycleBaseline(writesBefore, snapshot);
            stages.Add($"baseline={snapshot.Describe()}");
        });

        try
        {
            // 仅在 UI 线程设置抑制；非候选元素仍返回 TRUE。
            _window.Invoke(() => observer.SuppressCandidates = true);
            _trace("scenario", "native-ui-lifecycle-suppress", new Dictionary<string, object?>
            {
                ["previous"] = previousSuppress,
                ["current"] = observer.SuppressCandidates,
            });

            if (!PrepareComposition(out var prepareDetail))
            {
                return (false, prepareDetail);
            }

            if (!executor.Run())
            {
                return (false,
                    $"stopped at {executor.StoppedAtStage}: {executor.Stop} — {executor.StopDetail}; "
                    + $"candidate_visual=pending-not-assessed; shows={executor.SuccessfulShows}; "
                    + $"{string.Join("; ", stages)}");
            }

            // 最终再取一次安全点快照：必须满足与基线相同的完整不变量。
            var finalProbe = host.Probe("final");
            var finalSnapshot = finalProbe.Snapshot;
            var writes = finalProbe.Writes;
            var violation = executor.Baseline is { } baseline
                ? finalSnapshot.DescribeInvariantViolation(baseline)
                : "baseline not captured";
            var ok = finalProbe.Ok && violation is null;

            stages.Add(ok ? "final=unchanged"
                : $"final=probe_ok={finalProbe.Ok} {finalProbe.Detail} "
                    + $"violation={violation} snapshot={finalSnapshot.Describe()}");
            return (ok,
                $"native UIElement lifecycle only (shows={executor.SuccessfulShows}); "
                + $"candidate_visual=pending-not-assessed; {string.Join("; ", stages)} "
                + $"writes={writes}");
        }
        finally
        {
            // teardown 不属于被测阶段：恢复抑制值 + 测试结束组合清理。
            // 被测阶段的异常已由调用方处理；teardown 失败另行记录，不掩盖。
            TeardownLifecycle(observer, previousSuppress, stages);
        }
    }

    /// <summary>
    /// 场景 teardown：恢复 <c>SuppressCandidates</c>，并在仍组合时做一次受控清理。
    /// 失去前台时**不**发 Esc（<see cref="CleanupComposition"/> 内部会先核对自有前台），
    /// 无法安全清理时交由 host 的 session cleanup 处理，并如实记录。
    /// </summary>
    private void TeardownLifecycle(TsfUiElementObserver observer, bool previousSuppress,
        List<string> stages)
    {
        var result = NativeUiLifecycleTeardown.Run(
            previousSuppress,
            restore => _window.Invoke(() => observer.SuppressCandidates = restore),
            () => _store.IsComposing,
            () => NativeMethods.GetForegroundWindow() == _window.Hwnd,
            CleanupComposition,
            (kind, fields) => _trace("scenario", kind, fields));

        stages.Add($"teardown={result.Outcome}"
            + (result.Error is null ? string.Empty : $"({result.Error})"));
        if (result.ShouldReportCleanup)
        {
            _trace("scenario", "native-ui-lifecycle-teardown-outcome", new Dictionary<string, object?>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["error"] = result.Error,
            });
        }
    }

    private (bool Ok, string Detail) RunUiElementHideProbe(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        var observer = _session.UiElements;
        if (observer is null)
        {
            CleanupComposition();
            return (false, "ITfUIElementMgr/ITfUIElementSink unavailable");
        }

        WaitUntil(() => observer.CandidateCount > 0, 3_000);
        Capture("s9a-composing");

        var hide = _window.Invoke(() => observer.TryShowCandidates(false));
        Wait(500);
        Capture("s9b-show-false");

        var show = _window.Invoke(() => observer.TryShowCandidates(true));
        Wait(300);
        Capture("s9c-show-true");

        var writes = _recorder.Writes - writesBefore;
        var ok = _store.IsComposing && writes == 0;
        CleanupComposition();
        return (ok, $"hide={DescribeShowResults(hide)} show={DescribeShowResults(show)} candidates={observer.CandidateCount} begin={observer.BeginCount} update={observer.UpdateCount} end={observer.EndCount} writes={writes}");
    }

    private (bool Ok, string Detail) RunAsciiPlain(int writesBefore)
    {
        ResetDocumentForScenario();
        if (!_input.SendAsciiText("abc", "abc"))
        {
            return (false, "key injection aborted (foreground lost)");
        }

        Wait(600);
        var text = _store.Document.Text;
        var writes = _recorder.Writes - writesBefore;
        var ok = !_store.IsComposing && text == "abc" && writes == 3;
        return (ok, $"text='{text}' writes={writes} composing={_store.IsComposing} (requires English/no-IME mode)");
    }

    private (bool Ok, string Detail) RunUnicodeDirect(int writesBefore)
    {
        ResetDocumentForScenario();
        var sent = _input.SendUnicodeCodeUnit(0x4E2D, "zhong")
            && _input.SendUnicodeCodeUnit(0xD83D, "emoji-high")
            && _input.SendUnicodeCodeUnit(0xDE00, "emoji-low");
        Wait(600);
        var text = _store.Document.Text;
        var writes = _recorder.Writes - writesBefore;
        var ok = sent && !_store.IsComposing && text == "中😀" && writes == 2;
        return (ok, $"text='{text}' utf16={text.Length} writes={writes} composing={_store.IsComposing}");
    }

    private (bool Ok, string Detail) RunBlurClose(int writesBefore)
    {
        if (!PrepareComposition(out var detail))
        {
            return (false, detail);
        }

        Capture("s8a-composing");
        _window.FocusSecondWindow();
        Wait(800);
        Capture("s8b-blurred");
        var blurEnded = WaitUntil(() => !_store.IsComposing, 3_000);
        var writesAfterBlur = _recorder.Writes - writesBefore;
        _window.DestroySecondWindow();
        _window.RefocusMainWindow();
        Wait(300);

        if (!PrepareComposition(out detail))
        {
            return (false, $"second composition failed: {detail}");
        }

        Capture("s8c-before-close");
        _window.CloseWindow();
        var closed = WaitUntil(() => _window.Hwnd == IntPtr.Zero, 8_000);

        var ok = blurEnded && writesAfterBlur == 0 && closed && _session.CleanupFailures == 0
            && !_store.IsComposing;
        return (ok, $"blur_ended={blurEnded} writes_after_blur={writesAfterBlur} closed={closed} cleanup_failures={_session.CleanupFailures} composing={_store.IsComposing}");
    }

    private bool PrepareComposition(out string detail)
    {
        EnsureNoComposition();
        ResetDocumentForScenario();

        // 尚未发送任何键：只读核对当前活动 profile 是否为已审核的 Microsoft Pinyin。
        // opt-in 用 safepoint 门路由（同一 UI 安全点核对 foreground==root 且 focus==input），
        // 不用弱的 TryReadActiveProfileSnapshot；核验 profile 标签留作后续 SameIdentity 参照。
        if (_options.ChildInputSurface)
        {
            var probe = ProbeLifecycleSafePointOnUiThread(0, "prepare:" + _currentScenario,
                false, out var probeOk, out var probeDetail);
            if (!probeOk)
            {
                detail = $"environment unavailable: {probeDetail}";
                return false;
            }

            _scenarioIdentity = new ActiveProfileSnapshot(0,
                TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
                TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile,
                true, NativeMethods.GetFocus(), NativeMethods.CurrentWin32ThreadId());
            if (!ImeIdentity.IsVerifiableMicrosoftPinyin(probe.Profile))
            {
                detail = $"environment unavailable: active profile unverifiable ({probe.Profile})";
                return false;
            }
        }
        else
        {
            if (!TryReadActiveProfileSnapshot("prepare:" + _currentScenario, out var identity, out var identityDetail))
            {
                detail = $"environment unavailable: {identityDetail}";
                return false;
            }

            _scenarioIdentity = identity;
            if (!ImeIdentity.IsMicrosoftPinyin(identity))
            {
                detail = $"environment unavailable: {identityDetail}";
                return false;
            }
        }

        if (!_input.SendAsciiText("nihao", "nihao"))
        {
            detail = "key injection aborted (foreground lost)";
            return false;
        }

        var composed = WaitUntil(() => _store.IsComposing, 5_000);
        detail = composed ? "composition observed" : "no composition within 5000ms";
        return composed;
    }

    /// <summary>
    /// 只读查询当前活动 profile：必须在自有前台、UI 线程安全阶段执行；失败/不匹配返回 false。
    /// 不激活、不启用/禁用、不修改任何 profile，也不在 store 原生回调内调用。
    /// </summary>
    private bool TryReadActiveProfileSnapshot(string label, out ActiveProfileSnapshot snapshot, out string detail)
    {
        snapshot = default;
        if (NativeMethods.GetForegroundWindow() != _window.Hwnd)
        {
            detail = "own foreground is not active; refusing to query/send";
            _trace("identity", "refused-no-foreground", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["foreground"] = DescribeForeground(),
            });
            return false;
        }

        snapshot = _window.Invoke(() =>
        {
            var ownForeground = NativeMethods.GetForegroundWindow() == _window.Hwnd;
            var focus = NativeMethods.GetFocus();
            var (hr, profile) = TsfProfile.ReadActiveProfile(_log, label);
            return new ActiveProfileSnapshot(
                hr,
                profile.ProfileType,
                profile.LangId,
                profile.Clsid,
                profile.ProfileGuid,
                ownForeground,
                focus,
                NativeMethods.CurrentWin32ThreadId());
        });

        var match = ImeIdentity.IsMicrosoftPinyin(snapshot);
        _trace("identity", "snapshot", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["scenario"] = _currentScenario,
            ["hresult"] = TsfHResult.Describe(snapshot.HResult),
            ["profile_type"] = $"0x{snapshot.ProfileType:X}",
            ["langid"] = $"0x{snapshot.LangId:X4}",
            ["clsid"] = snapshot.Clsid.ToString(),
            ["profile_guid"] = snapshot.ProfileGuid.ToString(),
            ["win32_thread_id"] = snapshot.Win32ThreadId,
            ["own_foreground"] = snapshot.OwnForeground,
            ["match_microsoft_pinyin"] = match,
            ["expected_clsid"] = TsfProfile.MicrosoftPinyinClsid.ToString(),
            ["expected_profile_guid"] = TsfProfile.MicrosoftPinyinProfile.ToString(),
        });

        if (!snapshot.OwnForeground)
        {
            detail = "own foreground lost during identity query";
            return false;
        }

        detail = snapshot.HResult == 0
            ? (match
                ? "Microsoft Pinyin active"
                : $"active profile is not Microsoft Pinyin: {ImeIdentity.Describe(snapshot)}")
            : $"active profile query failed: {ImeIdentity.Describe(snapshot)}";
        return snapshot.HResult == 0;
    }

    private void ResetDocumentForScenario()
    {
        EnsureNoComposition();
        _window.Invoke(() => _store.ResetDocument(string.Empty));
        _window.RequestRelayout("scenario-reset");
        _window.Invoke(() => { });
    }

    private void EnsureNoComposition()
    {
        if (!_store.IsComposing)
        {
            return;
        }

        _input.PressVirtualKey(VkEscape, "escape-cleanup");
        WaitUntil(() => !_store.IsComposing, 2_000);
    }

    private void CleanupComposition()
    {
        EnsureNoComposition();
        _window.Invoke(() => { });
    }

    /// <summary>
    /// 可选 context/view 几何对照（默认关闭）。只在自有 UI 线程的安全阶段申请只读编辑会话，
    /// 查询同一临时范围的 view/store 几何；不刷新布局、不改文本/组合/选区/Binding、不重试。
    /// </summary>
    private void ProbeContextViewAt(string label)
    {
        if (!_options.ProbeContextView)
        {
            return;
        }

        _window.Invoke(() =>
        {
            var span = _store.CompositionSpan;
            var selection = _store.Document.Selection;
            var range = ContextViewProbe.ResolveRange(_store.IsComposing, span.Start, span.End,
                selection.End);
            ContextViewProbe.RunIfEnabled(true, label, _session.Context, _session.ClientId,
                _store, _trace, range);
        });
    }

    /// <summary>
    /// 诊断：在自有 UI 线程的安全阶段记录焦点/关联只读快照（默认关闭）。
    /// 只读；不 SetFocus/AssociateFocus，不在 sink/锁/几何回调内调用。
    /// </summary>
    private void CaptureFocusSnapshotAt(string label)
    {
        if (!_options.TraceLayoutChain)
        {
            return;
        }

        _window.Invoke(() =>
        {
            FocusSnapshot.Capture(_store.Chain, _trace, label, _window.Hwnd,
                _session.ThreadManager, _session.DocumentManager, _session.Context);
        });
    }

    private bool WaitOwnForeground(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (NativeMethods.GetForegroundWindow() == _window.Hwnd)
            {
                _trace("scenario", "foreground-ok", new Dictionary<string, object?>
                {
                    ["foreground"] = DescribeForeground(),
                });
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private bool EnsureOwnForeground(int timeoutMs)
    {
        if (NativeMethods.GetForegroundWindow() == _window.Hwnd)
        {
            return true;
        }

        return WaitOwnForeground(timeoutMs);
    }

    private bool WaitUntil(Func<bool> condition, int timeoutMs, int intervalMs = 50)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(intervalMs);
        }

        return condition();
    }

    private void Capture(string name)
    {
        var path = Path.Combine(_options.OutDir, name + ".png");
        var result = _window.Capture(path);
        _trace("capture", name, new Dictionary<string, object?>
        {
            ["path"] = result?.Path ?? "(failed)",
            ["x"] = result?.X,
            ["y"] = result?.Y,
            ["width"] = result?.Width,
            ["height"] = result?.Height,
        });
    }

    // -------------------------------------------------------------------
    // R27 生命周期执行器的宿主接口（都必须在 UI 线程调用）
    // -------------------------------------------------------------------

    /// <summary>供 <see cref="ScenarioLifecycleHost"/> 使用：场景窗口。</summary>
    internal TsfHostWindow Window => _window;

    /// <summary>
    /// 生命周期安全点的 UI 线程入口：所有 <see cref="ProbeLifecycleSafePoint"/> 调用都必须
    /// 经过本方法（经 <see cref="TsfHostWindow.Invoke"/> 投递），没有绕过派发器的路径。
    /// 已在 UI 线程时 <c>Invoke</c> 内联执行，因此宿主与截图共用同一入口是安全的。
    /// </summary>
    internal LifecycleSnapshot ProbeLifecycleSafePointOnUiThread(int writesBefore, string stage,
        bool runStopped, out bool ok, out string detail)
    {
        var snapshot = default(LifecycleSnapshot);
        var localOk = false;
        var localDetail = string.Empty;
        _window.Invoke(() =>
        {
            snapshot = ProbeLifecycleSafePoint(writesBefore, stage, runStopped,
                out localOk, out localDetail);
        });
        ok = localOk;
        detail = localDetail;
        return snapshot;
    }

    /// <summary>
    /// R27 返修：在 UI 线程的**单个安全点**内完成全部环境检查与快照读取。
    /// 只在 UI 消息安全点调用——绝不从 store 原生回调、锁授予或布局通知内部调用；
    /// 该方法本身不申请/释放锁、不发键、不改焦点。
    /// <para>
    /// 顺序：先确认 hwnd/自有前台/焦点/无运行停止/无 store 读锁/无布局通知重入，
    /// **然后**才在同一安全点内只读查询活动 profile；查询失败或非简体中文
    /// Microsoft Pinyin 完整身份 → <c>ok=false</c>；查询之后再复核一次前台/焦点。
    /// </para>
    /// </summary>
    /// <summary>
    /// opt-in child 模式下安全点使用的原生事实读取器（同一 UI body 内执行）。
    /// 注入点：测试以替身事实驱动 <see cref="ProbeLifecycleSafePoint"/> 的同一个门禁，
    /// 生产走真实 Win32 读取——共享判定路径，不是平行谓词。
    /// </summary>
    private LifecycleWindowFacts ReadLifecycleWindowFacts(IntPtr root, IntPtr input)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var focus = NativeMethods.GetFocus();
        var rootValid = root != IntPtr.Zero && NativeMethods.IsWindow(root);
        var inputValid = input != IntPtr.Zero && NativeMethods.IsWindow(input);
        uint rootTid = 0, rootPid = 0, inputTid = 0, inputPid = 0;
        if (root != IntPtr.Zero)
        {
            rootTid = NativeMethods.GetWindowThreadProcessId(root, out rootPid);
        }
        if (input != IntPtr.Zero)
        {
            inputTid = NativeMethods.GetWindowThreadProcessId(input, out inputPid);
        }
        return new LifecycleWindowFacts(rootValid, inputValid, rootTid, rootPid,
            inputTid, inputPid, foreground, focus);
    }

    internal LifecycleSnapshot ProbeLifecycleSafePoint(int writesBefore, string stage,
        bool runStopped, out bool ok, out string detail)
    {
        var hwnd = _window.Hwnd;
        var isChild = _options.ChildInputSurface;
        // opt-in：前台仍是 root，但键盘焦点必须在真实 input child（焦点只在 input 上
        // 时向 TSF 查询到的才是本进程 input 上下文）；默认 off 焦点==root，行为不变。
        var expectedFocus = isChild ? _window.InputHwnd : hwnd;
        var uiThreadId = _window.UiThreadId;
        var pid = Environment.ProcessId;

        // ---- 前置（profile COM 查询之前）----
        // child 模式：root AND input 都必须存活、属本进程、owning UI 线程同为
        // window.UiThreadId 且即当前线程；前台==root、焦点==input。外来/已回收
        // HWND 或 UI 线程不符一律拒绝，绝不发起 profile 查询。
        var preFacts = ReadLifecycleWindowFacts(hwnd, expectedFocus);
        var foreground = preFacts.Foreground;
        var focus = preFacts.Focus;
        var currentTid = NativeMethods.CurrentWin32ThreadId();

        // 前置快照只用于决定“是否允许发起 profile 查询”的门禁；
        // 它**不**作为返回证据——COM 查询可能重入并改变 store。
        var preStore = _store.SnapshotForLifecycle();
        var layoutDepth = _store.LayoutNotifyDepth;

        // 1) 先过环境前置检查（profile 查询之前）。窗口/运行状态用命令式逐项描述，
        //    前台/焦点/锁/重入四项交给可测试的纯门禁判定；child 归属检查同门并入。
        string? failure = null;
        var ownsInput = true;
        if (hwnd == IntPtr.Zero || !preFacts.RootValid)
        {
            failure = "window no longer exists";
        }
        else if (isChild && !preFacts.InputValid)
        {
            // input child 已销毁/句柄被回收：不得向 TSF 查询本进程输入上下文。
            failure = "input surface no longer exists";
        }
        else if (runStopped)
        {
            failure = "run state stopped";
        }
        else
        {
            var ownForeground = foreground == hwnd;
            var ownFocus = focus == expectedFocus;
            if (isChild)
            {
                // root+input 的 PID/TID/UI 线程归属（含 current Win32 thread==UiThreadId）。
                ownsInput = ChildSafePointOwnershipGate.OwnsChildSurface(
                    preFacts, hwnd, expectedFocus, uiThreadId, pid)
                    && currentTid == uiThreadId && uiThreadId != 0;
            }
            if (!LifecycleSafePointGate.Evaluate(ownForeground, ownFocus,
                    preStore.LockType, layoutDepth, ownsInput))
            {
                if (!ownForeground)
                {
                    failure = $"own foreground lost (foreground=0x{foreground.ToInt64():X})";
                }
                else if (!ownFocus)
                {
                    // 键盘焦点不在自有输入 surface：此时向 TSF 查询 profile 读到的不是本进程输入上下文。
                    failure = $"keyboard focus not ours (focus=0x{focus.ToInt64():X} "
                        + $"expected=0x{expectedFocus.ToInt64():X})";
                }
                else if (!ownsInput)
                {
                    failure = "input/root ownership check failed "
                        + $"(root_valid={preFacts.RootValid} input_valid={preFacts.InputValid} "
                        + $"root_tid={preFacts.RootTid} input_tid={preFacts.InputTid} "
                        + $"ui_tid={uiThreadId} current_tid={currentTid} "
                        + $"root_pid={preFacts.RootPid} input_pid={preFacts.InputPid} pid={pid})";
                }
                else if (preStore.LockType != 0)
                {
                    // 读锁/写锁未释放时绝不查询 profile：锁内调回 TSF 会重入同一线程的锁协议。
                    failure = $"store lock still held (lock_type=0x{preStore.LockType:X})";
                }
                else
                {
                    // 在 OnLayoutChange 等 TSF 通知的同步回调内不允许做安全点查询。
                    failure = $"reentrant native callback (layout_notify_depth={layoutDepth})";
                }
            }
        }

        // 2) 前置检查全部通过后才在同一安全点只读查询活动 profile。
        var profileHr = 0;
        var profileType = 0u;
        var profileLang = (ushort)0;
        var profileClsid = Guid.Empty;
        var profileGuid = Guid.Empty;
        var isMicrosoftPinyin = false;
        var profileText = ImeIdentity.UnverifiableProfile;
        var queried = failure is null;
        if (queried)
        {
            (profileHr, var profile) = TsfProfile.ReadActiveProfile(_log, "lifecycle:" + stage);
            profileType = profile.ProfileType;
            profileLang = profile.LangId;
            profileClsid = profile.Clsid;
            profileGuid = profile.ProfileGuid;
            var snapshot = new ActiveProfileSnapshot(profileHr, profileType, profileLang,
                profileClsid, profileGuid, true, focus, NativeMethods.CurrentWin32ThreadId());
            isMicrosoftPinyin = ImeIdentity.IsMicrosoftPinyin(snapshot);
            profileText = ImeIdentity.VerifiedIdentityTag(snapshot);

            if (profileHr != 0)
            {
                failure = $"active profile query failed: {TsfHResult.Describe(profileHr)}";
            }
            else if (!isMicrosoftPinyin)
            {
                // 非 Microsoft Pinyin（或语言/类型/任一 GUID 不符）：整个安全点失败，
                // 不能把未知 IME 接纳为基线。
                failure = "active profile is not Microsoft Pinyin (zh-CN input processor): "
                    + ImeIdentity.Describe(profileHr, profileType, profileLang,
                        profileClsid, profileGuid);
            }
        }

        // 3) 证据快照必须在 profile 查询**之后**重新采集：COM 查询可能重入并改变
        //    store/writes 与窗口/焦点/前台事实，查询前缓存的值不能作为有效证据。
        //    child 模式同样复核 input/root 归属——COM 重入期间窗口可能被销毁。
        var postFacts = ReadLifecycleWindowFacts(hwnd, expectedFocus);
        var postStore = _store.SnapshotForLifecycle();
        var writes = _recorder.Writes - writesBefore;
        var postLayoutDepth = _store.LayoutNotifyDepth;
        var postForeground = postFacts.Foreground;
        var postFocus = postFacts.Focus;
        var postForegroundOk = postForeground == hwnd;
        var postFocusOk = postFocus == expectedFocus;
        var postOwnsInput = !isChild
            || (ChildSafePointOwnershipGate.OwnsChildSurface(
                    postFacts, hwnd, expectedFocus, uiThreadId, pid)
                && NativeMethods.CurrentWin32ThreadId() == uiThreadId && uiThreadId != 0);
        // R29B：真实输入 surface 的 WM_SETFOCUS/WM_KILLFOCUS 事件计数（UI 线程读）。
        // 焦点瞬时离开再回来计数不同——focus==own 相等无法检测的不连续由它兜底。
        var focusEvents = _window.InputFocusEventCount;
        var store = postStore;

        if (failure is null && queried)
        {
            if (isChild && !postFacts.InputValid)
            {
                failure = "input surface lost during profile query";
            }
            else if (isChild && !postOwnsInput)
            {
                failure = "input/root ownership lost during profile query "
                    + $"(root_tid={postFacts.RootTid} input_tid={postFacts.InputTid} "
                    + $"ui_tid={uiThreadId} root_pid={postFacts.RootPid} "
                    + $"input_pid={postFacts.InputPid})";
            }
            else if (!postForegroundOk)
            {
                failure = $"own foreground lost during profile query "
                    + $"(foreground=0x{postForeground.ToInt64():X})";
            }
            else if (!postFocusOk)
            {
                failure = $"keyboard focus lost during profile query "
                    + $"(focus=0x{postFocus.ToInt64():X})";
            }
            else if (store.LockType != 0)
            {
                // 查询重入可能留下未释放的读/写锁；证据快照不得取自持锁临界区。
                failure = $"store lock still held after query (lock_type=0x{store.LockType:X})";
            }
            else if (postLayoutDepth != 0)
            {
                failure = $"reentrant native callback after query "
                    + $"(layout_notify_depth={postLayoutDepth})";
            }
        }

        ok = failure is null;
        detail = ok ? "ok" : $"{stage}: {failure}";

        _trace("scenario", "native-ui-lifecycle-probe", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["ok"] = ok,
            ["detail"] = detail,
            ["text"] = ImeFormat.EscapeUtf16(store.Text),
            ["span"] = $"{store.Start}..{store.End}",
            ["composing"] = store.Composing,
            ["lock_type"] = $"0x{store.LockType:X}",
            ["layout_notify_depth"] = postLayoutDepth,
            ["writes"] = writes,
            ["own_foreground"] = postForegroundOk,
            ["focus"] = $"0x{postFocus.ToInt64():X}",
            ["focus_is_own"] = postFocusOk,
            ["profile"] = profileText,
            ["profile_queried"] = queried,
            ["profile_hresult"] = queried ? TsfHResult.Describe(profileHr) : "(not-queried)",
            ["profile_is_microsoft_pinyin"] = isMicrosoftPinyin,
            ["composition_begin_count"] = store.BeginCount,
            ["composition_end_count"] = store.EndCount,
            ["composition_session_id"] = store.SessionId,
            ["input_focus_event_count"] = focusEvents,
        });

        return new LifecycleSnapshot(store.Text, store.Start, store.End, writes, store.Composing,
            profileText, store.BeginCount, store.EndCount, store.SessionId, focusEvents);
    }

    /// <summary>执行器日志入口。</summary>
    internal void TraceLifecycle(string kind, IDictionary<string, object?> fields)
        => _trace("scenario", kind, fields);

    /// <summary>
    /// 执行器截图入口（R27 返修）：整个截图流程——前后安全点、实际抓图、
    /// 组合不变量复核——都在 <see cref="TsfHostWindow.Invoke"/> 内执行，
    /// GetFocus/GetForegroundWindow/store/profile 全部在 UI 线程读取，
    /// 没有绕过派发器的调用路径。
    /// 任一失效则该截图**不记为有效证据**，并返回 false 让执行器停止后续 Show。
    /// 只读取自有窗口区域，不操作其他窗口。
    /// </summary>
    internal bool CaptureLifecycle(string name)
    {
        var ok = false;
        _window.Invoke(() => ok = CaptureLifecycleOnUiThread(name));
        return ok;
    }

    /// <summary>截图流程本体；必须已经在 UI 线程（由 <see cref="CaptureLifecycle"/> 派发）。</summary>
    private bool CaptureLifecycleOnUiThread(string name)
    {
        var path = Path.Combine(_options.OutDir, name + ".png");

        // 截图前：安全点必须成立，**且**当前组合必须与基线不变量完全一致——
        // 不允许在已被替换/已提交的组合上抓图（那会抓错内容，事后再恢复也无法补救）。
        var beforeProbe = ProbeLifecycleSafePoint(_lifecycleWritesBefore, name + ":pre-capture",
            false, out var preOk, out var preDetail);
        var preInvariantOk = preOk
            && _lifecycleBaseline is { } preBaseline
            && beforeProbe.SameInvariant(preBaseline);
        if (!preOk || !preInvariantOk)
        {
            var preViolation = !preOk
                ? preDetail
                : _lifecycleBaseline is { } vb
                    ? beforeProbe.DescribeInvariantViolation(vb) ?? "invariant mismatch"
                    : "baseline not captured";
            _trace("capture", name, new Dictionary<string, object?>
            {
                ["path"] = "(not-taken)",
                ["valid_evidence"] = false,
                ["reason"] = !preOk
                    ? "pre-capture safe point failed"
                    : "pre-capture invariant violated",
                ["detail"] = preDetail,
                ["invariant_violation"] = preViolation,
            });
            return false;
        }

        var result = _window.Capture(path);

        // 截图后：再次复核安全点与组合不变量；期间失焦/提交则证据无效。
        var afterProbe = ProbeLifecycleSafePoint(_lifecycleWritesBefore, name + ":post-capture",
            false, out var postOk, out var postDetail);
        var invariantOk = postOk
            && _lifecycleBaseline is { } baseline
            && afterProbe.SameInvariant(baseline);

        var ok = result is not null && preOk && postOk && invariantOk;
        _trace("capture", name, new Dictionary<string, object?>
        {
            ["path"] = result?.Path ?? "(failed)",
            ["x"] = result?.X,
            ["y"] = result?.Y,
            ["width"] = result?.Width,
            ["height"] = result?.Height,
            ["file_created"] = result is not null,
            ["valid_evidence"] = ok,
            ["pre_safe_point"] = preDetail,
            ["post_safe_point"] = postDetail,
            ["post_invariant_ok"] = invariantOk,
            ["own_foreground"] = NativeMethods.GetForegroundWindow() == _window.Hwnd,
        });
        return ok;
    }

    /// <summary>执行器设置本场景基线快照，供截图前后不变量复核。</summary>
    internal void SetLifecycleBaseline(int writesBefore, LifecycleSnapshot baseline)
    {
        _lifecycleWritesBefore = writesBefore;
        _lifecycleBaseline = baseline;
    }

    private int _lifecycleWritesBefore;
    private LifecycleSnapshot? _lifecycleBaseline;

    /// <summary>
    /// R29B opt-in：在<b>真实读锁</b>内查询组合 span 的 GetTextExt。
    /// 经 <see cref="TsfSessionHost.RequestAppRead"/> 发起 TF_ES_SYNC|TF_ES_READ 编辑会话，
    /// 在 DoEditSession 持锁回调内调用 store.GetTextExt——与 TIP 真实查询同前提。
    /// 已完全裁剪时按官方语义得 S_OK+全零矩形+clipped；未发布布局得 TS_E_NOLAYOUT。
    /// </summary>
    internal int InvokeReadLockedTextExt(int acpStart, int acpEnd,
        out TsfRect rect, out int clipped)
    {
        var resultRect = default(TsfRect);
        var resultClipped = 0;
        var hr = _session.RequestAppRead(store =>
            store.GetTextExt(store.ViewCookie, acpStart, acpEnd,
                out resultRect, out resultClipped));
        rect = resultRect;
        clipped = resultClipped;
        return hr;
    }

    /// <summary>
    /// R29B opt-in：一次完整证据截图 = root crop + 包含测试 root 的整台监视器，
    /// <b>每张</b>实际截图都经 <see cref="ChildSurfaceEvidenceCapture.RunStage"/> 的
    /// 前置安全点+不变量复核 → 真实抓图 → 后置复核；任一失败/抛出立即停止，
    /// root 抓图失败后<b>绝不</b>发起 monitor 抓图。真实 probe/capture 全在同一
    /// UI body 内执行；显示器矩形来自真实 MonitorFromWindow+GetMonitorInfoW，
    /// 不枚举候选 HWND。
    /// <para>
    /// R31 复审返修：<paramref name="phase"/> 是调用方宿主（当前执行器同一实例）
    /// 的实际 TSF 焦点阶段读取器——注入时 root/monitor <b>每张</b>抓图前后都经
    /// 共享 <see cref="TsfDocumentFocus.ReadPhase"/> 实际读回，并在阶段 COM
    /// 查询之后再复核组合/原生安全点；root 后置阶段失败即不发起 monitor 抓图。
    /// 默认 null（未接线宿主）保持既有仅组合/原生复核。
    /// </para>
    /// <para>
    /// R31 返修：返回 <see cref="CaptureVerdict"/>——失败时携带管线内<b>已捕获的
    /// 首败原因</b>（每张 RunStage 的 reason 原样带出，阶段读回失败标
    /// <see cref="CaptureVerdict.PhaseMismatch"/>）；执行器据此分类停止，
    /// 绝不因分类而再发任何 TSF/profile 复探。
    /// </para>
    /// </summary>
    internal CaptureVerdict CaptureChildSurface(string name,
        Func<TsfFocusPhaseResult>? phase = null)
    {
        var verdict = CaptureVerdict.Passed();
        _window.Invoke(() =>
        {
            if (_lifecycleBaseline is not { } baseline)
            {
                _trace("capture", name, new Dictionary<string, object?>
                {
                    ["valid_evidence"] = false,
                    ["reason"] = "baseline not captured",
                });
                verdict = new CaptureVerdict(false, "baseline not captured");
                return;
            }

            // probe：安全点（含 profile COM 查询前后复核）→ SafePointProbe。
            // 已在 UI 线程，直接调用共享入口；与测试替身走同一判定路径。
            // R34D：--host-drawn-candidates 下每张抓图前后追加真实 overlay+presenter
            // 体检——真实 HWND 可见性/矩形/快照身份都在同一 UI 安全点读，绝不只测
            // 内部布尔。任一缺失候选/渲染/自有窗口失败如实使本张证据失败。
            SafePointProbe ProbeUi()
            {
                var snapshot = ProbeLifecycleSafePoint(_lifecycleWritesBefore,
                    name + ":capture-probe", false, out var probeOk, out var probeDetail);
                var hostDrawnOk = true;
                var hostDrawnDetail = string.Empty;
                if (_options.HostDrawnCandidates)
                {
                    hostDrawnOk = InspectHostDrawnOverlay(name, baseline,
                        out hostDrawnDetail);
                    if (!hostDrawnOk)
                    {
                        probeOk = false;
                        probeDetail = probeDetail.Length > 0
                            ? probeDetail + "; " + hostDrawnDetail
                            : hostDrawnDetail;
                    }
                }

                return new SafePointProbe(probeOk, probeDetail, snapshot.Text,
                    snapshot.SpanStart, snapshot.SpanEnd, snapshot.Writes,
                    snapshot.Composing, snapshot.Profile,
                    snapshot.CompositionBeginCount, snapshot.CompositionEndCount,
                    snapshot.CompositionSessionId, snapshot.InputFocusChangeCount);
            }

            void RecordStage(string kind, IDictionary<string, object?> fields)
                => _trace("capture", kind, fields);

            // root crop：真实自有 root 窗口矩形抓图；每张前后先读实际 TSF 焦点
            // 阶段再复核组合/原生安全点（phase 由当前宿主实例注入）。
            var rootPath = Path.Combine(_options.OutDir, name + ".png");
            var root = ChildSurfaceEvidenceCapture.RunStage(name, "root", ProbeUi,
                () =>
                {
                    var cap = _window.Capture(rootPath);
                    return cap is { } c
                        ? new EvidenceCaptureResult(c.Path,
                            TsfRect.FromLtrb(c.X, c.Y, c.X + c.Width, c.Y + c.Height))
                        : null;
                },
                baseline, RecordStage, phase);
            if (!root.Ok)
            {
                _trace("capture", name, new Dictionary<string, object?>
                {
                    ["valid_evidence"] = false,
                    ["reason"] = "root capture stage failed",
                    ["detail"] = root.Reason,
                });
                verdict = new CaptureVerdict(false, root.Reason)
                {
                    PhaseMismatch = phase is not null
                        && root.Reason.Contains("tsf focus phase"),
                };
                return;
            }

            // monitor：root 抓图已确认为有效证据（含后置实际阶段读回）后才进行
            // 第二张——同一 phase 读取器继续按当前受控期望核对。
            var monitorPath = Path.Combine(_options.OutDir, name + "-monitor.png");
            var monitor = ChildSurfaceEvidenceCapture.RunStage(name, "monitor", ProbeUi,
                () =>
                {
                    if (!_window.TryGetMonitorRect(out var mr))
                    {
                        return null;
                    }

                    var cap = ScreenCapture.CaptureRegion(mr.Left, mr.Top,
                        mr.Width, mr.Height, monitorPath);
                    return cap is { } c ? new EvidenceCaptureResult(c.Path, mr) : null;
                },
                baseline, RecordStage, phase);

            verdict = monitor.Ok
                ? CaptureVerdict.Passed()
                : new CaptureVerdict(false, monitor.Reason)
                {
                    PhaseMismatch = phase is not null
                        && monitor.Reason.Contains("tsf focus phase"),
                };
            _trace("capture", name, new Dictionary<string, object?>
            {
                ["path"] = root.Path ?? "(failed)",
                ["root_crop"] = root.Ok ? root.Rect.ToString() : "(failed)",
                ["monitor_path"] = monitor.Path ?? "(failed)",
                ["monitor_rect"] = monitor.Ok ? monitor.Rect.ToString() : "(failed)",
                ["valid_evidence"] = verdict.Ok,
                ["monitor_detail"] = monitor.Reason,
                ["own_foreground"] = NativeMethods.GetForegroundWindow() == _window.Hwnd,
            });

            // 有效 child 证据才补一条完整 stage 诊断（仍在同一 UI body 内、同一
            // 安全点窗口内读全部真实几何/TSF 事实；失败字段如实记 unknown，绝不
            // 用 stale/zero raw 矩形冒充有效几何）。
            if (verdict.Ok && _options.ChildInputSurface)
            {
                EmitChildCaptureDiagnostics(name, baseline);
            }
        });
        return verdict;
    }

    /// <summary>本场景基线快照（R34D：per-scenario 保存的真实候选基线）。</summary>
    private CandidateSnapshot? _hostDrawnBaseline;

    /// <summary>
    /// R34D：在 owning UI 线程上核对真实 overlay + presenter——读真实
    /// <see cref="OverlayInspection"/>（含真实 HWND/IsWindowVisible/屏幕矩形/当前
    /// 快照 id+代数+条目数）与 presenter 的 live 状态；与 per-scenario 基线比较
    /// 代数稳定性。只在 --host-drawn-candidates 下调用；任一真实失败/不一致如实返回
    /// false，绝不把内部标志当真实状态。本方法本身不写任何窗口/TSF/COM 状态。
    /// </summary>
    private bool InspectHostDrawnOverlay(string stage, LifecycleSnapshot baseline,
        out string detail)
    {
        detail = string.Empty;
        var inspection = _window.InspectCandidateOverlay(stage);
        var presenter = _session.UiElements?.HostDrawnPresenter;
        var liveSnapshot = presenter?.Current;
        var inputVisible = _options.ChildInputSurface
            && _window.TryGetVisibleInputSurfaceRect(out var visibleRect, out _)
            && !visibleRect.IsEmpty;

        _trace("capture", "host-drawn-inspect", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["overlay_hwnd_ok"] = inspection.Ok,
            ["overlay_failure"] = inspection.Failure,
            ["native_visible"] = inspection.Visible,
            ["visible"] = !inspection.VisibleRect.IsEmpty,
            ["element_id"] = inspection.ElementId,
            ["generation"] = inspection.Generation,
            ["entries"] = inspection.EntryCount,
            ["hidden_by_clip"] = inspection.HiddenByClip,
            ["input_visible_empty"] = !inputVisible,
            ["live_has_candidates"] = presenter?.HasLiveCandidates,
        });

        if (!inspection.Ok)
        {
            detail = $"overlay inspection failed: {inspection.Failure ?? "unknown"}";
            return false;
        }

        // 基线：本场景第一张证据前要求真实快照+候选存在并保存身份；之后每张
        // 都要求元素 id/代数与基线一致（替换过的元素绝不能沿用旧快照）。
        if (_hostDrawnBaseline is null)
        {
            if (liveSnapshot is not { Valid: true } || liveSnapshot.Entries.Count == 0)
            {
                detail = "no live candidates baseline (presenter published no valid snapshot)";
                return false;
            }

            _hostDrawnBaseline = liveSnapshot;
        }
        else
        {
            var base_ = _hostDrawnBaseline;
            var current = liveSnapshot;
            if (current is null || current.ElementId != base_.ElementId
                || current.Generation != base_.Generation)
            {
                detail = $"element/generation unstable "
                    + $"(baseline id={base_.ElementId} gen={base_.Generation}, "
                    + $"now id={current?.ElementId} gen={current?.Generation})";
                return false;
            }
        }

        // 展示判定与真实 input 可见交集一致：全裁剪时 overlay 必须真实隐藏；
        // 可见时必须真实可见+非空矩形+真实候选，且锚定在真实行附近。
        var visible = inspection.Visible && !inspection.VisibleRect.IsEmpty;
        if (!inputVisible)
        {
            if (visible || !inspection.HiddenByClip)
            {
                detail = $"input fully clipped but overlay state invalid "
                    + $"(native_visible={inspection.Visible} rect={inspection.VisibleRect} "
                    + $"hidden_by_clip={inspection.HiddenByClip})";
                return false;
            }

            return true;
        }

        if (!visible)
        {
            detail = $"input visible but overlay not really shown "
                + $"(native_visible={inspection.Visible} rect={inspection.VisibleRect})";
            return false;
        }

        // 可见时必须与 presenter 的<b>当前</b> live 快照同源：有效、非空、
        // 同元素同代数——绝不只和场景基线比，旧元素缓存/失败快照不得通过。
        if (liveSnapshot is not { Valid: true } || liveSnapshot.Entries.Count == 0)
        {
            detail = "overlay visible but presenter has no valid live candidates";
            return false;
        }

        if (inspection.ElementId != liveSnapshot.ElementId
            || inspection.Generation != liveSnapshot.Generation)
        {
            detail = $"overlay snapshot identity mismatch "
                + $"(displayed id={inspection.ElementId} gen={inspection.Generation}, "
                + $"live id={liveSnapshot.ElementId} gen={liveSnapshot.Generation})";
            return false;
        }

        if (inspection.EntryCount == 0)
        {
            detail = "overlay visible but snapshot has no candidates";
            return false;
        }

        // 锚定核对：overlay 必须在 root 客户区内，且下边邻近真实 input 行
        // （上下不超出一屏行高*4 的容忍；防“overlay 画到了屏幕别处”）。
        // root/row 真实查询失败即如实失败——绝不跳过锚定检查放行。
        if (!_window.TryGetRootWindowScreenRect(out var rootScreen))
        {
            detail = "overlay anchor check failed: root window rect query failed";
            return false;
        }

        if (!_window.TryGetTextRowScreenRect(out var rowScreen))
        {
            detail = "overlay anchor check failed: text row rect query failed";
            return false;
        }

        var overlayRect = inspection.VisibleRect;
        var withinRoot = overlayRect.Left >= rootScreen.Left
            && overlayRect.Right <= rootScreen.Right
            && overlayRect.Top >= rootScreen.Top
            && overlayRect.Bottom <= rootScreen.Bottom;
        var nearRow = Math.Abs(overlayRect.Top - rowScreen.Bottom) <= 96
            || Math.Abs(rowScreen.Top - overlayRect.Bottom) <= 96;
        if (!withinRoot || !nearRow)
        {
            detail = $"overlay not anchored near input row "
                + $"(overlay={overlayRect} row={rowScreen} root={rootScreen})";
            return false;
        }

        return true;
    }

    /// <summary>
    /// R29B review-6：有效 child 抓图的完整 stage 诊断——root/viewport/input 原始矩形、
    /// input∩祖先可见交集、文本行、滚动 X/Y、root/input 句柄、真实 PID/TID、
    /// TSF AssociatedHwnd/store.GetWnd/DPI 与原子快照+连续性计数。
    /// 只在两张抓图都判有效后的同一 UI 安全点内执行；每个查询显式成败，失败即
    /// 如实记 <c>query_failed</c>，绝不以 stale/zero 几何冒充成功。只读：
    /// 不 mutate TSF 关联、不在锁/原生回调内查询 profile。
    /// </summary>
    private void EmitChildCaptureDiagnostics(string stage, LifecycleSnapshot baseline)
    {
        var fields = new Dictionary<string, object?>
        {
            ["stage"] = stage,
        };

        // ---- 原始几何（显式成败；查询失败字段记 query_failed，不给假矩形）----
        if (_window.TryGetRootWindowScreenRect(out var rootRect))
        {
            fields["root_rect"] = rootRect.ToString();
        }
        else
        {
            fields["root_rect"] = "query_failed";
        }

        if (_window.TryGetViewportClientScreenRect(out var viewportRect))
        {
            fields["viewport_rect"] = viewportRect.ToString();
        }
        else
        {
            fields["viewport_rect"] = "query_failed";
        }

        if (_window.TryGetInputSurfaceScreenRect(out var inputRect))
        {
            fields["input_rect"] = inputRect.ToString();
        }
        else
        {
            fields["input_rect"] = "query_failed";
        }

        if (_window.TryGetVisibleInputSurfaceRect(out var visibleRect, out var visibleFailure))
        {
            fields["input_intersection_rect"] = visibleRect.ToString();
        }
        else
        {
            fields["input_intersection_rect"] = "query_failed";
            fields["input_intersection_reason"] = visibleFailure ?? "(unknown)";
        }

        if (_window.TryGetTextRowScreenRect(out var rowRect))
        {
            fields["row_rect"] = rowRect.ToString();
        }
        else
        {
            fields["row_rect"] = "query_failed";
        }

        // ---- 滚动/句柄/线程 ----
        fields["scroll_x"] = _window.ScrollOffsetX;
        fields["scroll_y"] = _window.ScrollOffsetY;
        fields["root_hwnd"] = $"0x{_window.Hwnd.ToInt64():X}";
        fields["input_hwnd"] = $"0x{_window.InputHwnd.ToInt64():X}";
        fields["own_pid"] = Environment.ProcessId;
        fields["ui_tid"] = _window.UiThreadId;
        fields["current_tid"] = NativeMethods.CurrentWin32ThreadId();
        var rootTid = NativeMethods.GetWindowThreadProcessId(_window.Hwnd, out var rootPid);
        uint inputTid = 0;
        uint inputPid = 0;
        if (_window.InputHwnd != IntPtr.Zero)
        {
            inputTid = NativeMethods.GetWindowThreadProcessId(_window.InputHwnd, out inputPid);
        }
        fields["root_tid"] = rootTid;
        fields["root_pid"] = rootPid;
        fields["input_tid"] = inputTid;
        fields["input_pid"] = inputPid;

        // ---- TSF 关联/store 视角/DPI（只读；不 mutate 关联）----
        fields["tsf_associated_hwnd"] = _session.AssociatedHwnd == IntPtr.Zero
            ? "(none)" : $"0x{_session.AssociatedHwnd.ToInt64():X}";
        var storeWnd = _store.GetWnd(_store.ViewCookie, out var storeHwnd);
        fields["store_getwnd_hresult"] = TsfHResult.Describe(storeWnd);
        fields["store_getwnd_hwnd"] = $"0x{storeHwnd.ToInt64():X}";
        fields["dpi"] = _window.CurrentDpi;

        // ---- 原子快照 + 计数（与 probe 同一读取路径，一次性再取）----
        var store = _store.SnapshotForLifecycle();
        fields["text"] = ImeFormat.EscapeUtf16(store.Text);
        fields["span"] = $"{store.Start}..{store.End}";
        fields["composing"] = store.Composing;
        fields["writes"] = _recorder.Writes;
        fields["lock_type"] = $"0x{store.LockType:X}";
        fields["layout_notify_depth"] = _store.LayoutNotifyDepth;
        fields["composition_begin_count"] = store.BeginCount;
        fields["composition_end_count"] = store.EndCount;
        fields["composition_session_id"] = store.SessionId;
        fields["input_focus_event_count"] = _window.InputFocusEventCount;
        fields["baseline_span"] = $"{baseline.SpanStart}..{baseline.SpanEnd}";
        fields["baseline_profile"] = baseline.Profile;

        _trace("capture", stage + ":stage-diagnostics", fields);
    }

    private static void Wait(int milliseconds) => Thread.Sleep(Math.Max(1, milliseconds));

    private string DescribeForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var thread = NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        return $"0x{foreground.ToInt64():X} pid={pid} tid={thread} own={foreground == _window.Hwnd}";
    }

    private static string DescribeShowResults(IReadOnlyList<UiElementShowResult> results)
    {
        if (results.Count == 0)
        {
            return "(no live ui elements)";
        }

        return string.Join("; ", results.Select(r =>
        {
            var hrText = r.ShowHrAvailable
                ? TsfHResult.Describe(r.HResult)
                : $"hr-unavailable({r.ShowExceptionType ?? r.ShowExceptionStage ?? "none"})";
            var ended = r.EndedDuringShow ? " ended-during-show" : string.Empty;
            return $"id={r.Id} candidate={r.IsCandidateList} hr={hrText} shown={r.ShownBefore}->{r.ShownAfter}{ended}";
        }));
    }
}
