using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>
/// 场景运行器。每个场景只做**一个**几何动作，其余全部保持不动：
/// 不取消/提交/重打组合，不改焦点，不动系统候选窗口。
/// <para>
/// 所有等待/派发/输入/动作都经过同一个 deadline 与取消检查，窗口关闭或超时立即停止，
/// 不会对无效 HWND 继续操作。
/// </para>
/// </summary>
internal sealed class NativeEditRunner
{
    private const int ChildMovePixels = 11;
    private const int TargetLineFromTop = EditBaseline.TargetLine;

    private readonly NativeEditOptions _options;
    private readonly ProbeLog _log;
    private readonly NativeEditObservationLog _observations;
    private readonly List<string> _failures = new();
    private readonly RunState _state;
    private int _captureIndex;
    private int _compositionStartAcp;
    private int _baselineFirstVisibleLine;
    private string _baselineCompositionText = string.Empty;

    // 组合建立完成那一刻的事件计数基线：其后再出现 EN_CHANGE/焦点事件都视为
    // 被测动作引发的异常（child-collapse 不变量门用），而不是瞬时 GetFocus 快照。
    private int _baselineEnChangeCount;
    private int _baselineFocusChangeCount;

    // R28 child-collapse：动作前记录的自有 EDIT 窗口矩形，仅用于恢复原始尺寸。
    // 绝不虚构；记录失败则动作前即判失败，不做 collapse。
    private NativeRect _collapseOriginalRect;
    private bool _collapseOriginalRectValid;

    internal NativeEditRunner(NativeEditOptions options, ProbeLog log,
        NativeEditObservationLog observations)
    {
        _options = options;
        _log = log;
        _observations = observations;
        _state = new RunState(options.TimeoutMs);
    }

    internal IReadOnlyList<string> Failures => _failures;

    internal bool Aborted => _state.Aborted;

    internal bool WindowClosed => _state.WindowClosed;

    internal bool TimedOut => _state.TimedOut;

    /// <summary>统一取消判定：窗口已关、超时或已中止。</summary>
    internal bool ShouldStop => _state.ShouldStop;

    internal int Run()
    {
        using var host = new NativeEditHost(_log, _observations);
        // 父窗口留足上方区域（viewport 位于 y=96），使 clip 时候选的预期位置仍在父截图范围内。
        if (!host.Create(900, 620, visible: !_options.HiddenSmoke))
        {
            return NativeEditOutcome.Precondition;
        }

        return _options.HiddenSmoke ? RunHiddenSmoke(host) : RunScenarios(host);
    }

    /// <summary>
    /// 隐藏 smoke：不显示、不请求前台、不输入。
    /// 额外做**真实原生**检查：EM_GETSEL 双端、EM_POSFROMCHAR 索引/负坐标/滚动后 y 变化。
    /// </summary>
    private int RunHiddenSmoke(NativeEditHost host)
    {
        Check("window-created", host.IsCreated, "hidden");
        Check("edit-subclassed", host.EditSubclassed, $"{host.EditSubclassed}");
        Check("viewport-subclassed", host.ViewportSubclassed, $"{host.ViewportSubclassed}");
        Check("window-hidden", !NativeMethods.IsWindowVisible(host.Parent), "visible=false");
        Check("never-shown", !host.WasShown, $"was_shown={host.WasShown}");

        var text = BuildBaselineText();
        Check("edit-text", host.SetEditText(text), $"len={host.GetEditText().Length}");

        // ---- 真实原生契约检查（不显示窗口，仅消息查询） ----
        var lineStart = host.LineIndex(TargetLineFromTop);
        var lineEnd = lineStart + LineLength(TargetLineFromTop);
        host.SetSelection(lineStart, lineEnd);
        var selection = host.GetSelection();
        Check("em-getsel-both-ends",
            selection.Ok && selection.Value.Start == lineStart && selection.Value.End == lineEnd,
            selection.Ok
                ? $"start={selection.Value.Start} end={selection.Value.End} expected={lineStart}..{lineEnd}"
                : selection.Failure);

        var posA = host.PosFromChar(lineStart);
        var posB = host.PosFromChar(lineStart + 2);
        Check("em-posfromchar-known",
            posA.Ok && posB.Ok,
            posA.Ok && posB.Ok ? $"{posA.Value.X},{posA.Value.Y} / {posB.Value.X},{posB.Value.Y}"
                : $"{posA.Failure} {posB.Failure}");
        Check("em-posfromchar-x-varies",
            posA.Ok && posB.Ok && posA.Value.X != posB.Value.X,
            posA.Ok && posB.Ok ? $"x {posA.Value.X}->{posB.Value.X}" : "unavailable");

        host.SetFirstVisibleLine(TargetLineFromTop);
        var beforeScroll = host.PosFromChar(lineStart);
        var firstBefore = host.FirstVisibleLine;
        var scrolled = host.LinesScroll(0, 1);
        var afterScroll = host.PosFromChar(lineStart);
        var firstAfter = host.FirstVisibleLine;
        Check("internal-scroll-moves-text-not-window",
            firstAfter == firstBefore + 1
            && beforeScroll.Ok && afterScroll.Ok
            && afterScroll.Value.Y != beforeScroll.Value.Y,
            $"scroll_result={scrolled} y {beforeScroll.Value.Y}->{afterScroll.Value.Y} "
            + $"firstline {firstBefore}->{firstAfter}");

        // 负坐标：把光标行滚到视口上方，EM_POSFROMCHAR 的 y 必须为负且解析正确。
        host.SetFirstVisibleLine(TargetLineFromTop + 20);
        var negative = host.PosFromChar(lineStart);
        Check("em-posfromchar-negative-y",
            negative.Ok && negative.Value.Y < 0,
            negative.Ok ? $"y={negative.Value.Y}" : negative.Failure);

        // 隐藏 smoke 也接统一状态：pump 失败或超时必须明确记为失败，而不是静默继续。
        var pumped = host.Pump(120, _state);
        Check("hidden-smoke-pump", pumped, _state.DescribeStop());
        Check("hidden-smoke-not-stopped", !_state.ShouldStop, _state.DescribeStop());

        var ok = _failures.Count == 0;
        _log.Record("run", "native-edit", "result", new Dictionary<string, object?>
        {
            ["mode"] = "hidden-smoke",
            ["scenario_ran"] = false,
            ["key_input_sent"] = false,
            ["foreground_requested"] = false,
            ["stop"] = _state.DescribeStop(),
            ["checks_pass"] = ok,
            ["candidate_visual"] = "not-required",
            ["pass"] = ok,
            ["failed_checks"] = _failures.Count == 0 ? null : string.Join(",", _failures),
        });
        return ok ? NativeEditOutcome.Pass : NativeEditOutcome.Failed;
    }

    private int RunScenarios(NativeEditHost host)
    {
        host.Show();
        host.RequestForeground();
        // 前台与 EDIT 焦点必须**同时**成立才继续；用户先点标题栏时继续等待点到 EDIT。
        if (!WaitOwnForegroundAndEditFocus(host))
        {
            return NativeEditOutcome.Precondition;
        }

        var scenarios = _options.Scenario is { } single ? new[] { single } : NativeEditScenario.All;
        var executed = 0;
        foreach (var scenario in scenarios)
        {
            if (ShouldStop)
            {
                break;
            }

            _log.Record("run", "native-edit", "scenario-begin",
                new Dictionary<string, object?> { ["scenario"] = scenario });
            var ok = RunScenario(host, scenario);
            executed++;
            _log.Record("scenario", "native-edit", "result", new Dictionary<string, object?>
            {
                ["scenario"] = scenario,
                ["checks_pass"] = ok,
                ["candidate_visual"] = "pending",
                ["assessment"] = ok ? "pending-visual" : "fail",
                ["pass"] = false,
                ["note"] = "visual verdict is human-only; this tool never claims follow/hide pass",
                ["failed_checks"] = _failures.Count == 0 ? null : string.Join(",", _failures),
            });
        }

        var checksPass = _failures.Count == 0 && !ShouldStop;
        var exit = NativeEditOutcome.Resolve(checksPass, executed > 0, false, Aborted);
        _log.Record("run", "native-edit", "result", new Dictionary<string, object?>
        {
            ["mode"] = "scenario",
            ["scenarios_executed"] = executed,
            ["aborted"] = Aborted,
            ["window_closed"] = WindowClosed,
            ["timed_out"] = TimedOut,
            ["checks_pass"] = checksPass,
            ["candidate_visual"] = executed > 0 ? "pending" : "not-required",
            ["pass"] = false,
            ["exit_code"] = exit,
            ["failed_checks"] = _failures.Count == 0 ? null : string.Join(",", _failures),
        });
        return exit;
    }

    private bool RunScenario(NativeEditHost host, string scenario)
    {
        // finally 只负责几何恢复：collapse 已应用但流程在动作/失焦/超时处提前返回或抛异常时，
        // 必须把自有 EDIT 还原到原始尺寸（同一 HWND、同一原点）。绝不恢复焦点、不发键。
        // 原异常不经包装直接重抛；几何恢复失败被单独记录为一次失败，绝不报成成功。
        _collapseOriginalRectValid = false;
        try
        {
            return RunScenarioBody(host, scenario);
        }
        finally
        {
            // 只恢复几何：同一 HWND 原样回写记录的宽/高，不恢复焦点、不发键。
            // 判定依据是“是否记录过原始矩形”（_collapseOriginalRectValid）+ HWND 仍属我们，
            // 不是“该矩形是否非空”——零面积矩形同样是合法的 SetWindowPos 目标。
            if (_collapseOriginalRectValid && NativeMethods.IsWindow(host.Edit))
            {
                var restore = host.ResizeEditChild(_collapseOriginalRect.Width,
                    _collapseOriginalRect.Height,
                    NativeEditScenario.ActionKind(scenario) + ":finally-restore");
                _log.Record("action", "native-edit", "child-collapse-finally-restore",
                    new Dictionary<string, object?>
                    {
                        ["scenario"] = scenario,
                        ["expected"] = _collapseOriginalRect.ToString(),
                        ["restore_ok"] = restore.Ok,
                        ["restore_failure"] = restore.Failure,
                        ["actual"] = restore.Ok ? restore.Value.ToString() : null,
                        ["note"] = "geometric restore only; no focus recovery, no keys sent",
                    });
                if (!restore.Ok)
                {
                    // 恢复失败必须单独记为失败；原异常/返回值不受影响，绝不算成功。
                    _failures.Add("child-collapse-finally-restore");
                }
            }
        }
    }

    private bool RunScenarioBody(NativeEditHost host, string scenario)
    {
        var before = _failures.Count;
        _observations.ResetForScenario();

        if (!PrepareComposition(host, scenario, out var detail))
        {
            Fail("prepare-composition", detail);
            return false;
        }

        host.Phase = "before";
        if (!host.Pump(400, _state))
        {
            Fail("pump-before", _state.DescribeStop());
            return false;
        }

        var profileBefore = NativeEditProfile.Read(_log, $"{scenario}:before");
        Check("profile-microsoft-pinyin-before", profileBefore.IsMicrosoftPinyin,
            NativeEditProfile.Describe(profileBefore));
        if (!profileBefore.IsMicrosoftPinyin)
        {
            // 身份不匹配：在动作前停止，不靠 Check(false) 继续。
            Fail("profile-gate-before", NativeEditProfile.Describe(profileBefore));
            return false;
        }

        CaptureGeometry(host, scenario, "before");
        // child-collapse：截图前先做不变量门（InputGate/live profile/live compstr/无 end/result/无新增事件）。
        // 默认三场景保持既有顺序：先截图再由后面的 Check 判定。
        if (scenario == NativeEditScenario.ChildCollapse
            && !CollapseInvariantGate(host, scenario, "pre-before-capture"))
        {
            return false;
        }

        Capture(host, $"{scenario}-before");

        if (scenario == NativeEditScenario.ChildCollapse
            && !CollapseInvariantGate(host, scenario, "pre-action"))
        {
            return false;
        }

        var actionOk = ApplyAction(host, scenario, out var actionDetail);
        Check("action", actionOk, actionDetail);
        if (!actionOk)
        {
            return false;
        }

        // 动作后立即做一次门：若 collapse 本身已破坏组合/焦点，在泵与截图前停止。
        if (scenario == NativeEditScenario.ChildCollapse
            && !CollapseInvariantGate(host, scenario, "post-action"))
        {
            return false;
        }

        host.Phase = "after";
        if (!host.Pump(700, _state))
        {
            Fail("pump-after", _state.DescribeStop());
            return false;
        }

        if (scenario == NativeEditScenario.ChildCollapse
            && !CollapseInvariantGate(host, scenario, "post-pump"))
        {
            return false;
        }

        CaptureGeometry(host, scenario, "after");
        if (scenario == NativeEditScenario.ChildCollapse
            && !CollapseInvariantGate(host, scenario, "pre-after-capture"))
        {
            return false;
        }

        Capture(host, $"{scenario}-after");

        var profileAfter = NativeEditProfile.Read(_log, $"{scenario}:after");
        Check("profile-microsoft-pinyin-after", profileAfter.IsMicrosoftPinyin,
            NativeEditProfile.Describe(profileAfter));
        Check("profile-unchanged", profileAfter.SameAs(profileBefore),
            $"before=({NativeEditProfile.Describe(profileBefore)}) after=({NativeEditProfile.Describe(profileAfter)})");

        // 实时组合有效性：独立查询（不复用历史缓存），空/错误一律不可用。
        var afterComposition = _observations.QueryComposition(host.Edit, "after");
        Check("composition-live-after", afterComposition.Usable,
            afterComposition.Describe());
        Check("composition-string-unchanged",
            afterComposition.Usable && afterComposition.Text == _baselineCompositionText,
            $"before='{ImeFormat.EscapeUtf16(_baselineCompositionText)}' "
            + $"after='{ImeFormat.EscapeUtf16(afterComposition.Text)}'");
        Check("no-end-or-result-after",
            _observations.CompositionEnd == 0 && _observations.ResultStringCount == 0,
            $"end={_observations.CompositionEnd} results={_observations.ResultStringCount}");

        if (NativeEditScenario.HasRestore(scenario))
        {
            if (scenario == NativeEditScenario.ChildCollapse
                && !CollapseInvariantGate(host, scenario, "pre-restore"))
            {
                return false;
            }

            var restoreOk = RestoreAction(host, scenario, out var restoreDetail);
            Check("restore-action", restoreOk, restoreDetail);
            if (!restoreOk)
            {
                return false;
            }

            if (scenario == NativeEditScenario.ChildCollapse
                && !CollapseInvariantGate(host, scenario, "post-restore"))
            {
                return false;
            }

            host.Phase = "restore";
            if (!host.Pump(700, _state))
            {
                Fail("pump-restore", _state.DescribeStop());
                return false;
            }

            if (scenario == NativeEditScenario.ChildCollapse
                && !CollapseInvariantGate(host, scenario, "post-restore-pump"))
            {
                return false;
            }

            CaptureGeometry(host, scenario, "restore");
            if (scenario == NativeEditScenario.ChildCollapse
                && !CollapseInvariantGate(host, scenario, "pre-restore-capture"))
            {
                return false;
            }

            Capture(host, $"{scenario}-restore");

            // restore 之后同样必须复核：组合仍活动、串未变、无 end/result、profile 未变。
            var profileRestore = NativeEditProfile.Read(_log, $"{scenario}:restore");
            Check("profile-microsoft-pinyin-restore", profileRestore.IsMicrosoftPinyin,
                NativeEditProfile.Describe(profileRestore));
            Check("profile-unchanged-restore", profileRestore.SameAs(profileBefore),
                $"before=({NativeEditProfile.Describe(profileBefore)}) restore=({NativeEditProfile.Describe(profileRestore)})");

            var restoreComposition = _observations.QueryComposition(host.Edit, "restore");
            Check("composition-live-restore", restoreComposition.Usable,
                restoreComposition.Describe());
            Check("composition-string-unchanged-restore",
                restoreComposition.Usable && restoreComposition.Text == _baselineCompositionText,
                $"before='{ImeFormat.EscapeUtf16(_baselineCompositionText)}' "
                + $"restore='{ImeFormat.EscapeUtf16(restoreComposition.Text)}'");
            Check("no-end-or-result-after-restore",
                _observations.CompositionEnd == 0 && _observations.ResultStringCount == 0,
                $"end={_observations.CompositionEnd} results={_observations.ResultStringCount}");
        }

        // 收尾：Esc 清理单独标记，不混入验收计数。
        host.Phase = "cleanup";
        SendEscape(host);

        return _failures.Count == before;
    }

    private bool PrepareComposition(NativeEditHost host, string scenario, out string detail)
    {
        if (!InputGate.Check(host.Parent, host.Edit, out var gate))
        {
            detail = "input gate failed: " + gate;
            return false;
        }

        host.Phase = "baseline";
        if (!host.SetEditText(BuildBaselineText()))
        {
            detail = "baseline text not applied";
            return false;
        }

        // 基线保持首可见行 0，目标行（line3）留在 viewport 上部但**不贴顶**，
        // 使“内部滚动一行”仍让组合行完整可见（否则滚动 1 行就裁出，无法作小滚动对照）。
        host.SetFirstVisibleLine(0);

        // 光标置于**目标行行末**（lineN 长度 = 5，不能用固定 4）。
        var lineStart = host.LineIndex(TargetLineFromTop);
        var lineEnd = lineStart + LineLength(TargetLineFromTop);
        host.SetSelection(lineEnd, lineEnd);
        // EM_SCROLLCARET 让目标行进入可见区；随后回读真实首可见行，基线记录确切位置。
        host.SendCommand(NativeEditNative.EM_SCROLLCARET);
        if (!host.Pump(200, _state))
        {
            detail = "state stopped during baseline: " + _state.DescribeStop();
            return false;
        }

        var selection = host.GetSelection();
        if (!selection.Ok || selection.Value.Start != lineEnd)
        {
            detail = selection.Ok
                ? $"caret not at line end (start={selection.Value.Start} expected={lineEnd})"
                : "em-getsel failed: " + selection.Failure;
            return false;
        }

        // 目标行必须真实可见（其矩形与 viewport 交集非空），否则小滚动对照不成立。
        var targetRow = host.CharPointToScreen(lineEnd);
        if (!targetRow.Ok)
        {
            detail = "target row position unavailable: " + targetRow.Failure;
            return false;
        }

        var targetRowRect = host.RowRectForChar(lineEnd);
        var viewportClient = host.ClientScreen(host.Viewport);
        if (!targetRowRect.Ok || targetRowRect.Value.Intersect(viewportClient).IsEmpty)
        {
            detail = $"target row not visible in viewport (row={targetRowRect.Value} "
                + $"viewport={viewportClient})";
            return false;
        }

        _baselineFirstVisibleLine = host.FirstVisibleLine;
        _compositionStartAcp = lineEnd;

        var profile = NativeEditProfile.Read(_log, $"{scenario}:baseline");
        Check("profile-microsoft-pinyin-baseline", profile.IsMicrosoftPinyin,
            NativeEditProfile.Describe(profile));
        if (!profile.IsMicrosoftPinyin)
        {
            // 身份不匹配：在注入与动作前停止，不靠 Check(false) 继续。
            detail = "active profile is not Microsoft Pinyin: " + NativeEditProfile.Describe(profile);
            return false;
        }

        CaptureGeometry(host, scenario, "baseline");

        var input = new NativeEditInput(_log, host, $"native-edit-{scenario}", _state);
        if (!input.SendAsciiText("nihao", "nihao"))
        {
            _state.MarkAborted();
            detail = "key injection aborted (foreground/focus lost)";
            return false;
        }

        if (!host.Pump(900, _state))
        {
            detail = "state stopped while waiting for composition: " + _state.DescribeStop();
            return false;
        }

        // 准备阶段必须确认：本次确有 start、未 end/未 result、且真实非空组合串。
        var prepared = _observations.QueryComposition(host.Edit, "prepare");
        if (_observations.CompositionStart == 0 || _observations.CompositionEnd != 0
            || _observations.ResultStringCount != 0 || !prepared.Usable)
        {
            detail = $"composition not established: start={_observations.CompositionStart} "
                + $"end={_observations.CompositionEnd} results={_observations.ResultStringCount} "
                + $"compstr={prepared.Describe()}";
            return false;
        }

        _baselineCompositionText = prepared.Text;
        // 组合建立后的事件基线：child-collapse 据此区分“组合初始化期的 EN_CHANGE/焦点事件”
        // 与“被测动作之后才冒出来的额外事件”。
        _baselineEnChangeCount = _observations.EnChangeCount;
        _baselineFocusChangeCount = _observations.FocusChangeCount;
        detail = $"line={TargetLineFromTop} caret={lineEnd} first_visible={_baselineFirstVisibleLine} "
            + $"compstr='{ImeFormat.EscapeUtf16(_baselineCompositionText)}'";
        return true;
    }

    private bool ApplyAction(NativeEditHost host, string scenario, out string detail)
    {
        if (_state.ShouldStop)
        {
            detail = "state stopped before action: " + _state.DescribeStop();
            return false;
        }

        switch (scenario)
        {
            case NativeEditScenario.InternalScroll:
                // 小滚动对照：记录组合行矩形与 viewport 交集，动作后必须仍非空且上移一行。
                var rowBefore = host.RowRectForChar(_compositionStartAcp);
                var viewportBefore = host.ClientScreen(host.Viewport);
                var windowsBefore = (host.WindowRect(host.Parent), host.WindowRect(host.Viewport),
                    host.WindowRect(host.Edit));
                var firstBefore = host.FirstVisibleLine;
                var scrollResult = host.LinesScroll(0, 1);
                var firstAfter = host.FirstVisibleLine;
                var rowAfter = host.RowRectForChar(_compositionStartAcp);
                var windowsAfter = (host.WindowRect(host.Parent), host.WindowRect(host.Viewport),
                    host.WindowRect(host.Edit));

                _log.Record("action", "native-edit", "internal-scroll", new Dictionary<string, object?>
                {
                    ["check_action"] = NativeEditScenario.ActionKind(scenario),
                    ["lines_scrolled"] = 1,
                    ["result"] = scrollResult,
                    ["first_visible_line"] = $"{firstBefore}->{firstAfter}",
                    ["row_before"] = rowBefore.Ok ? rowBefore.Value.ToString() : rowBefore.Failure,
                    ["row_after"] = rowAfter.Ok ? rowAfter.Value.ToString() : rowAfter.Failure,
                    ["viewport"] = viewportBefore.ToString(),
                    ["windows_unchanged"] = windowsBefore == windowsAfter,
                    ["note"] = "EDIT/viewport/parent do not move; only the internal scroll offset changes",
                });

                if (firstAfter != firstBefore + 1)
                {
                    detail = $"first visible line did not advance ({firstBefore}->{firstAfter})";
                    return false;
                }

                if (!rowBefore.Ok || !rowAfter.Ok)
                {
                    detail = $"row rect unavailable: {rowBefore.Failure} {rowAfter.Failure}";
                    return false;
                }

                // 组合行必须在动作前后都完整可见于 viewport（小滚动不应裁出）。
                if (rowBefore.Value.Intersect(viewportBefore).IsEmpty
                    || rowAfter.Value.Intersect(viewportBefore).IsEmpty)
                {
                    detail = $"composition row clipped by viewport: before={rowBefore.Value} "
                        + $"after={rowAfter.Value} viewport={viewportBefore}";
                    return false;
                }

                if (rowAfter.Value.Top >= rowBefore.Value.Top)
                {
                    detail = $"row did not move up: {rowBefore.Value} -> {rowAfter.Value}";
                    return false;
                }

                if (windowsBefore != windowsAfter)
                {
                    detail = "window geometry changed during internal scroll";
                    return false;
                }

                detail = $"em_linescroll=0,1 firstline {firstBefore}->{firstAfter} "
                    + $"row {rowBefore.Value}->{rowAfter.Value} (still inside viewport)";
                return true;

            case NativeEditScenario.ChildMove:
                var moved = host.MoveEditChildBy(0, -ChildMovePixels);
                if (!moved.Ok)
                {
                    detail = moved.Failure ?? "move failed";
                    return false;
                }

                detail = $"EDIT child moved by (0,-{ChildMovePixels}) physical px; "
                    + "parent/viewport unchanged (child-window move comparison, not an internal-scroll fix)";
                return true;

            case NativeEditScenario.ChildClip:
                // 只把 EDIT 上移到“组合行底边刚好位于 viewport 顶边之上 8px”，
                // 使组合行完全裁出，而父窗口仍保留足够上方区域，让候选的预期位置仍在父截图内。
                var row = host.RowRectForChar(_compositionStartAcp);
                if (!row.Ok)
                {
                    detail = "composition row unavailable: " + row.Failure;
                    return false;
                }

                var viewportClient = host.ClientScreen(host.Viewport);
                var viewportWindow = host.WindowRect(host.Viewport);
                var editBefore = host.WindowRect(host.Edit);
                if (viewportClient.IsEmpty || editBefore.IsEmpty)
                {
                    detail = "viewport or edit geometry unavailable";
                    return false;
                }

                // 需要的屏幕位移：让 row.Bottom 落到 viewportClient.Top - 8。
                var desiredRowBottom = viewportClient.Top - 8;
                var deltaY = desiredRowBottom - row.Value.Bottom;

                var editOrigin = new NativeMethods.POINT { X = editBefore.Left, Y = editBefore.Top };
                if (!NativeMethods.ScreenToClient(host.Viewport, ref editOrigin))
                {
                    detail = "screen-to-client(edit) failed";
                    return false;
                }

                var clip = host.MoveEditChild(editOrigin.X, editOrigin.Y + deltaY);
                if (!clip)
                {
                    detail = "clip move failed";
                    return false;
                }

                var rowClipped = host.RowRectForChar(_compositionStartAcp);
                var viewportAfter = host.ClientScreen(host.Viewport);
                var parentWindow = host.WindowRect(host.Parent);

                if (!rowClipped.Ok)
                {
                    detail = "clipped row unavailable: " + rowClipped.Failure;
                    return false;
                }

                // 组合行必须完全在 viewport 之外，但仍位于父窗口范围内（证据截图能覆盖）。
                if (!rowClipped.Value.Intersect(viewportAfter).IsEmpty)
                {
                    detail = $"composition row not fully clipped: row={rowClipped.Value} "
                        + $"viewport={viewportAfter}";
                    return false;
                }

                if (rowClipped.Value.Bottom <= parentWindow.Top
                    || rowClipped.Value.Bottom >= viewportAfter.Top)
                {
                    detail = $"clipped row not inside evidence region: row={rowClipped.Value} "
                        + $"parent={parentWindow} viewport={viewportAfter}";
                    return false;
                }

                _log.Record("action", "native-edit", "child-clip", new Dictionary<string, object?>
                {
                    ["check_action"] = NativeEditScenario.ActionKind(scenario),
                    ["row_before"] = row.Value.ToString(),
                    ["row_after"] = rowClipped.Value.ToString(),
                    ["viewport_client"] = viewportAfter.ToString(),
                    ["viewport_window"] = viewportWindow.ToString(),
                    ["parent_window"] = parentWindow.ToString(),
                    ["edit_window"] = host.WindowRect(host.Edit).ToString(),
                    ["delta_y"] = deltaY,
                    ["note"] = "only the own EDIT moves; the system candidate window is never touched",
                });

                detail = $"EDIT moved by dy={deltaY} so the composition row is fully clipped "
                    + $"and still inside the parent evidence region; row={rowClipped.Value} "
                    + $"viewport={viewportAfter}";
                return true;

            case NativeEditScenario.ChildCollapse:
                // R28 有界对照：只把自有 EDIT 高度改为 0，宽度与原点不变。
                // 假设（非 Windows 保证）：可视面积为零时系统候选可能消失，而组合与焦点保持。
                // 禁止：SWP_HIDEWINDOW/SWP_SHOWWINDOW、样式改动、SetFocus/AssociateFocus、
                // 重新打字、任何对候选 HWND 或 globalIME 的操作。
                var collapseParent = host.WindowRect(host.Parent);
                var collapseViewport = host.WindowRect(host.Viewport);
                var collapseEditBefore = host.WindowRect(host.Edit);
                var collapseEditClientBefore = host.ClientScreen(host.Edit);
                if (collapseEditBefore.IsEmpty || collapseEditClientBefore.IsEmpty)
                {
                    detail = "edit geometry unavailable before collapse";
                    return false;
                }

                var collapseTarget = CollapseResizePlan.CollapseTarget(
                    collapseEditBefore.Width, collapseEditBefore.Height);
                if (collapseTarget is null)
                {
                    detail = "cannot collapse: original geometry not positive";
                    return false;
                }

                _collapseOriginalRect = collapseEditBefore;
                _collapseOriginalRectValid = true;

                // 同一个 SetWindowPos 同时给出原点（NOMOVE 时 x/y 被忽略，传原值仅为可读性）。
                var collapsed = host.ResizeEditChild(
                    collapseTarget.Value.Width, collapseTarget.Value.Height,
                    NativeEditScenario.ActionKind(scenario));
                if (!collapsed.Ok)
                {
                    detail = collapsed.Failure ?? "collapse failed";
                    return false;
                }

                var collapseEditAfter = collapsed.Value;
                var collapseEditClientAfter = host.ClientScreen(host.Edit);
                var collapseParentAfter = host.WindowRect(host.Parent);
                var collapseViewportAfter = host.WindowRect(host.Viewport);
                var originUnchanged = collapseEditAfter.Left == collapseEditBefore.Left
                    && collapseEditAfter.Top == collapseEditBefore.Top;
                var widthUnchanged = collapseEditAfter.Width == collapseEditBefore.Width;
                var windowHeightZero = collapseEditAfter.Height == 0;
                var clientHeightZero = collapseEditClientAfter.Height == 0;
                var parentUnchanged = collapseParent == collapseParentAfter;
                var viewportUnchanged = collapseViewport == collapseViewportAfter;
                var collapseVerified = CollapseResizePlan.GeometryPreservedAfterCollapse(
                    collapseEditAfter.Height, collapseEditClientAfter.Height,
                    collapseEditBefore.Left, collapseEditBefore.Top, collapseEditBefore.Width,
                    collapseEditAfter.Left, collapseEditAfter.Top, collapseEditAfter.Width,
                    parentUnchanged, viewportUnchanged);

                _log.Record("action", "native-edit", "child-collapse", new Dictionary<string, object?>
                {
                    ["check_action"] = NativeEditScenario.ActionKind(scenario),
                    ["edit_before"] = collapseEditBefore.ToString(),
                    ["edit_client_before"] = collapseEditClientBefore.ToString(),
                    ["edit_after"] = collapseEditAfter.ToString(),
                    ["edit_client_after"] = collapseEditClientAfter.ToString(),
                    ["parent_window"] = collapseParentAfter.ToString(),
                    ["viewport_window"] = collapseViewportAfter.ToString(),
                    ["window_height_zero"] = windowHeightZero,
                    ["client_height_zero"] = clientHeightZero,
                    ["origin_unchanged"] = originUnchanged,
                    ["width_unchanged"] = widthUnchanged,
                    ["parent_unchanged"] = parentUnchanged,
                    ["viewport_unchanged"] = viewportUnchanged,
                    ["collapse_verified"] = collapseVerified,
                    ["note"] = "only the own EDIT is asked to shrink to zero height; the geometric "
                        + "result is verified below; no composition/focus claim is made here",
                });
                foreach (var kv in host.FocusWindowSnapshot())
                {
                    _log.Record("action", "native-edit", "child-collapse-focus", new Dictionary<string, object?>
                    {
                        ["check_action"] = NativeEditScenario.ActionKind(scenario),
                        ["field"] = kv.Key,
                        ["value"] = kv.Value,
                    });
                }

                if (!collapseVerified)
                {
                    detail = $"collapse not fully verified: window={collapseEditAfter} "
                        + $"client={collapseEditClientAfter} "
                        + $"expected height=0 (window+client), width={collapseEditBefore.Width}, "
                        + $"origin=({collapseEditBefore.Left},{collapseEditBefore.Top}); "
                        + $"flags window_h0={windowHeightZero} client_h0={clientHeightZero} "
                        + $"origin_ok={originUnchanged} width_ok={widthUnchanged} "
                        + $"parent_ok={parentUnchanged} viewport_ok={viewportUnchanged}";
                    return false;
                }

                detail = $"own EDIT requested height=0 via {NativeEditScenario.ActionKind(scenario)}; "
                    + $"verified window+client height=0, origin/width unchanged, parent/viewport unchanged; "
                    + $"edit={collapseEditAfter} client={collapseEditClientAfter}";
                return true;

            default:
                detail = "unknown scenario";
                return false;
        }
    }

    private bool RestoreAction(NativeEditHost host, string scenario, out string detail)
    {
        switch (scenario)
        {
            case NativeEditScenario.ChildClip:
                var restored = host.MoveEditChild(0, 0);
                var edit = host.WindowRect(host.Edit);
                detail = $"EDIT restored to parent client (0,0); edit={edit}";
                return restored && !edit.IsEmpty;

            case NativeEditScenario.ChildCollapse:
                // 只恢复几何：同一 HWND、同一原点、恢复记录的原始宽/高。不做任何焦点恢复。
                // 判定依据是“是否记录过原始矩形”，不是“该矩形是否非空”——
                // 即使原始矩形因某种原因记录成零面积，仍按记录值回写。
                if (!_collapseOriginalRectValid)
                {
                    detail = "original edit rect was never recorded; nothing safe to restore";
                    return false;
                }

                var restoreTarget = CollapseResizePlan.RestoreTarget(
                    _collapseOriginalRect.Width, _collapseOriginalRect.Height);
                var resizedBack = host.ResizeEditChild(restoreTarget.Width,
                    restoreTarget.Height, NativeEditScenario.ActionKind(scenario) + ":restore");
                if (!resizedBack.Ok)
                {
                    detail = resizedBack.Failure ?? "restore resize failed";
                    return false;
                }

                var restoredRect = resizedBack.Value;
                var posMatches = restoredRect.Left == _collapseOriginalRect.Left
                    && restoredRect.Top == _collapseOriginalRect.Top;
                _log.Record("action", "native-edit", "child-collapse-restore", new Dictionary<string, object?>
                {
                    ["check_action"] = NativeEditScenario.ActionKind(scenario) + ":restore",
                    ["expected"] = _collapseOriginalRect.ToString(),
                    ["actual"] = restoredRect.ToString(),
                    ["position_unchanged"] = posMatches,
                    ["size_restored"] = restoredRect.Width == _collapseOriginalRect.Width
                        && restoredRect.Height == _collapseOriginalRect.Height,
                });
                detail = $"EDIT size restored to {_collapseOriginalRect.Width}x{_collapseOriginalRect.Height} "
                    + $"at original pos; actual={restoredRect}";
                var ok = CollapseResizePlan.RestoredToOriginal(
                    _collapseOriginalRect.Left, _collapseOriginalRect.Top,
                    _collapseOriginalRect.Width, _collapseOriginalRect.Height,
                    restoredRect.Left, restoredRect.Top,
                    restoredRect.Width, restoredRect.Height);
                if (ok)
                {
                    // 正常恢复已完成；finally 不再重复几何恢复。
                    _collapseOriginalRectValid = false;
                }

                return ok;

            default:
                detail = "no restore defined for scenario";
                return false;
        }
    }

    private void SendEscape(NativeEditHost host)
    {
        if (!InputGate.Check(host.Parent, host.Edit, out var gate))
        {
            _log.Record("cleanup", "native-edit", "escape-skipped", new Dictionary<string, object?>
            {
                ["reason"] = gate,
                ["counted_in_acceptance"] = false,
            });
            return;
        }

        var input = new NativeEditInput(_log, host, "native-edit-cleanup", _state);
        var sent = input.PressScanCode(NativeEditNative.VK_ESCAPE, "escape");
        host.Pump(300, _state);
        _log.Record("cleanup", "native-edit", "escape", new Dictionary<string, object?>
        {
            ["sent"] = sent,
            ["counted_in_acceptance"] = false,
            ["note"] = "cleanup only; excluded from pass/fail counters",
        });
    }

    private void CaptureGeometry(NativeEditHost host, string scenario, string phase)
    {
        var editClientScreen = host.ClientScreen(host.Edit);
        var viewportClientScreen = host.ClientScreen(host.Viewport);
        var parentWindow = host.WindowRect(host.Parent);

        // 三项坐标分别来自真实来源：组合起点 ACP、原生 caret、EM_POSFROMCHAR。
        var compositionStart = host.CharPointToScreen(_compositionStartAcp);
        var nativeCaret = host.NativeCaret();
        var emCaret = host.GetSelection();
        var emCaretPoint = emCaret.Ok ? host.CharPointToScreen(emCaret.Value.Start) : default;

        var snapshot = new GeometrySnapshot
        {
            Phase = phase,
            ParentWindow = parentWindow,
            ParentClient = host.ClientScreen(host.Parent),
            ViewportWindow = host.WindowRect(host.Viewport),
            ViewportClient = viewportClientScreen,
            EditWindow = host.WindowRect(host.Edit),
            EditClient = editClientScreen,
            EditViewportIntersection = editClientScreen.Intersect(viewportClientScreen),
            FirstVisibleLine = host.FirstVisibleLine,
            LineCount = host.LineCount,
            SelectionStart = emCaret.Ok ? emCaret.Value.Start : -1,
            SelectionEnd = emCaret.Ok ? emCaret.Value.End : -1,
            Text = host.GetEditText(),
            CompositionStartAcp = _compositionStartAcp,
            CompositionPointKnown = compositionStart.Ok,
            CompositionPointScreenX = compositionStart.Ok ? compositionStart.Value.X : 0,
            CompositionPointScreenY = compositionStart.Ok ? compositionStart.Value.Y : 0,
            NativeCaretKnown = nativeCaret.Ok,
            NativeCaretScreenX = nativeCaret.Ok ? nativeCaret.Value.X : 0,
            NativeCaretScreenY = nativeCaret.Ok ? nativeCaret.Value.Y : 0,
            EmCaretKnown = emCaret.Ok && emCaretPoint.Ok,
            EmCaretScreenX = emCaret.Ok && emCaretPoint.Ok ? emCaretPoint.Value.X : 0,
            EmCaretScreenY = emCaret.Ok && emCaretPoint.Ok ? emCaretPoint.Value.Y : 0,
            NativeCaretFailure = nativeCaret.Failure,
            EmCaretFailure = emCaret.Ok ? emCaretPoint.Failure : emCaret.Failure,
        };

        _observations.CaptureStrings(host.Edit, phase, "geometry-snapshot");
        _log.Record("geometry", "native-edit", "snapshot", snapshot.ToFields());
        _log.Record("geometry", "native-edit", "ime-counts", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["scenario"] = scenario,
            ["composition_start"] = _observations.CompositionStart,
            ["composition_updates"] = _observations.CompositionUpdate,
            ["composition_end"] = _observations.CompositionEnd,
            ["composition_strings"] = _observations.CompositionStringCount,
            ["result_strings"] = _observations.ResultStringCount,
            ["char_messages"] = _observations.CharCount,
            ["en_change"] = _observations.EnChangeCount,
            ["focus_changes"] = _observations.FocusChangeCount,
            ["last_composition"] = ImeFormat.EscapeUtf16(_observations.LastCompositionString),
            ["last_nonempty_composition"] =
                ImeFormat.EscapeUtf16(_observations.LastNonEmptyComposition),
            ["last_result"] = ImeFormat.EscapeUtf16(_observations.LastResultString),
        });
    }

    private void Capture(NativeEditHost host, string name)
    {
        Directory.CreateDirectory(_options.OutDir);
        var path = Path.Combine(_options.OutDir, $"{_captureIndex:00}-{name}.png");
        _captureIndex++;
        var result = ScreenCapture.CaptureWindowRegion(host.Parent, path);
        var ok = result is not null && File.Exists(path);
        _log.Record("capture", "native-edit", name, new Dictionary<string, object?>
        {
            ["path"] = result?.Path ?? "(failed)",
            ["x"] = result?.X,
            ["y"] = result?.Y,
            ["width"] = result?.Width,
            ["height"] = result?.Height,
            ["ok"] = ok,
            ["note"] = "parent window region only; system candidate window is never moved",
        });
        Check("capture:" + name, ok, result?.Path ?? "capture failed");
    }

    /// <summary>
    /// 等待“自有窗口前台 **且** 焦点已在 EDIT”同时成立。
    /// <para>
    /// 初始化阶段允许**一次**普通的自有子控件聚焦：仅当当前前台确为自有 Parent、
    /// Parent/EDIT 同进程同 UI 线程时，才调用 <c>SetFocus(EDIT)</c>；随后验证焦点确实在 EDIT。
    /// 若前台请求被 Windows 拒绝、或前台已不是自有窗口，则**绝不** SetFocus/重夺前台，
    /// 只继续等待用户主动点击。用户先点标题栏时，等待循环会做同样的自有前台检查并尝试这一次初始化。
    /// </para>
    /// <para>
    /// 全程不 TOPMOST、不 AttachThreadInput、不合成点击、不对任何外部窗口 SetFocus；
    /// 场景开始后不再改变焦点。
    /// </para>
    /// </summary>
    private bool WaitOwnForegroundAndEditFocus(NativeEditHost host)
    {
        var deadline = Math.Min(Environment.TickCount64 + _options.ForegroundWaitMs,
            Environment.TickCount64 + _state.RemainingMs);
        var foregroundSeen = false;
        var initialFocusAttempted = false;
        while (Environment.TickCount64 < deadline && !_state.ShouldStop)
        {
            if (!host.PumpOnce())
            {
                _state.MarkWindowClosed();
                return false;
            }

            var ownForeground = NativeMethods.GetForegroundWindow() == host.Parent;
            if (ownForeground && !foregroundSeen)
            {
                foregroundSeen = true;
                _log.Record("run", "native-edit", "foreground-ok", new Dictionary<string, object?>
                {
                    ["foreground"] = $"0x{host.Parent.ToInt64():X}",
                });
            }

            if (ownForeground && NativeEditNative.GetFocus() == host.Edit)
            {
                _log.Record("run", "native-edit", "edit-focused", new Dictionary<string, object?>
                {
                    ["focus"] = $"0x{host.Edit.ToInt64():X}",
                    ["source"] = initialFocusAttempted ? "initial-own-child-focus" : "user-click",
                    ["note"] = initialFocusAttempted
                        ? "focus came from the one-time initial own-child focus"
                        : "focus produced by the user's own click",
                });
                return true;
            }

            // 初始化：最多一次，且必须已确认自有前台。
            if (ownForeground && !initialFocusAttempted)
            {
                var outcome = host.TryInitialChildFocusOnce(ref initialFocusAttempted);
                if (outcome.Attempted)
                {
                    _log.Record("run", "native-edit", "initial-focus-result", new Dictionary<string, object?>
                    {
                        ["ok"] = outcome.Ok,
                        ["detail"] = outcome.Detail,
                        ["attempts"] = 1,
                        ["source"] = "initial-own-child-focus",
                    });

                    if (outcome.Ok)
                    {
                        return true;
                    }
                }
                else
                {
                    // 前置条件不满足：记录原因，继续等待用户主动点击（不再尝试）。
                    _log.Record("run", "native-edit", "initial-focus-skipped", new Dictionary<string, object?>
                    {
                        ["detail"] = outcome.Detail,
                        ["note"] = "no SetFocus and no foreground reclaim; waiting for a user click",
                    });
                }
            }

            Thread.Sleep(50);
        }

        _log.Record("run", "native-edit", "foreground-or-focus-missing", new Dictionary<string, object?>
        {
            ["foreground_seen"] = foregroundSeen,
            ["initial_focus_attempted"] = initialFocusAttempted,
            ["stop"] = _state.DescribeStop(),
            ["note"] = "no key event was sent; not accepted",
        });
        return false;
    }

    /// <summary>
    /// child-collapse 的不变量门：每次被测动作/派帧/截图**之前**运行。
    /// 任一项失败都返回 false，调用方必须立刻停止后续截图与输入（finally 几何恢复除外）。
    /// 检查项：
    ///   1. InputGate —— 自有父窗口仍前台、自有 EDIT 仍焦点、同一线程归属；
    ///   2. 活动配置文件仍是微软拼音（live，非缓存）；
    ///   3. 实时重查 GCS_COMPSTR，必须与基线组合串逐字相同（不是只查“非空”）；
    ///   4. 无 CompositionEnd / 无 RESULTSTR；
    ///   5. 无组合建立之后才出现的新 EN_CHANGE（区分初始化期 EN_CHANGE 与动作后额外 EN_CHANGE）；
    ///   6. 焦点变化**事件计数**自基线起为 0（检测的是事件流，不是瞬时 GetFocus）。
    /// 仅用于 <see cref="NativeEditScenario.ChildCollapse"/>；默认三场景的顺序与语义不变。
    /// </summary>
    private bool CollapseInvariantGate(NativeEditHost host, string scenario, string tag)
    {
        // 1. 输入安全门：前台/焦点/线程归属；只做检查，不夺取。
        if (!InputGate.Check(host.Parent, host.Edit, out var gate))
        {
            Fail($"{scenario}-invariant-inputgate:{tag}", "input gate lost: " + gate);
            return false;
        }

        // 2. 活动输入法仍是微软拼音（实时读，不是缓存快照）。
        var profile = NativeEditProfile.Read(_log, $"{scenario}:{tag}");
        if (!profile.IsMicrosoftPinyin)
        {
            Fail($"{scenario}-invariant-profile:{tag}",
                "profile changed: " + NativeEditProfile.Describe(profile));
            return false;
        }

        // 3. 实时重查同一组合串：必须仍有可用非空 GCS_COMPSTR，且与基线逐字相同。
        var live = _observations.QueryComposition(host.Edit, $"collapse-gate:{tag}");
        if (!live.Usable)
        {
            Fail($"{scenario}-invariant-composition:{tag}",
                "composition not readable: " + live.Describe());
            return false;
        }

        if (!string.Equals(live.Text, _baselineCompositionText, StringComparison.Ordinal))
        {
            Fail($"{scenario}-invariant-composition-text:{tag}",
                $"compstr changed: baseline='{ImeFormat.EscapeUtf16(_baselineCompositionText)}' "
                    + $"live='{ImeFormat.EscapeUtf16(live.Text)}'");
            return false;
        }

        // 4. 组合未被结束/提交。
        if (_observations.CompositionEnd != 0 || _observations.ResultStringCount != 0)
        {
            Fail($"{scenario}-invariant-commit:{tag}",
                $"composition ended/committed: end={_observations.CompositionEnd} "
                    + $"results={_observations.ResultStringCount}");
            return false;
        }

        // 5. 组合建立之后才出现的新 EN_CHANGE 视为动作引发的内容扰动。
        var extraEnChange = _observations.EnChangeCount - _baselineEnChangeCount;
        if (extraEnChange != 0)
        {
            Fail($"{scenario}-invariant-enchange:{tag}",
                $"unexpected EN_CHANGE after collapse: +{extraEnChange} "
                    + $"(baseline={_baselineEnChangeCount} now={_observations.EnChangeCount})");
            return false;
        }

        // 6. 焦点变化按“事件流”计数，不是瞬时 GetFocus：EN_SETFOCUS/EN_KILLFOCUS 一个都不能多。
        var focusEvents = _observations.FocusChangeCount - _baselineFocusChangeCount;
        if (focusEvents != 0)
        {
            Fail($"{scenario}-invariant-focus-events:{tag}",
                $"focus-change events after collapse: +{focusEvents} "
                    + $"(baseline={_baselineFocusChangeCount} now={_observations.FocusChangeCount})");
            return false;
        }

        return true;
    }

    private void Check(string name, bool ok, string? detail)
    {
        _log.Record("check", "native-edit", ok ? "pass" : "fail", new Dictionary<string, object?>
        {
            ["check"] = name,
            ["pass"] = ok,
            ["detail"] = detail,
        });
        if (!ok)
        {
            _failures.Add(name);
        }
    }

    private void Fail(string name, string detail) => Check(name, false, detail);

    private static string BuildBaselineText() => EditBaseline.Text();

    private static int LineLength(int line) => EditBaseline.LineLength(line);
}
