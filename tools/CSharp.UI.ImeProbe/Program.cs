using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;

namespace CSharp.UI.ImeProbe;

internal sealed record ScenarioResult(string Name, string Status, string Notes, Dictionary<string, object?> Details);

/// <summary>TI-01 Windows IME 可行性探针（独立工具，不引用生产框架）。</summary>
internal static class Program
{
    private static readonly Dictionary<string, string> StatusDescriptions = new()
    {
        ["PASS"] = "the scenario's explicit assertion held",
        ["OBSERVED"] = "message chain / API behaviour observed, but the scenario's end-to-end acceptance semantics were not established",
        ["UNVERIFIED"] = "input condition missing; nothing was faked",
        ["SKIP"] = "precondition missing (foreground/IME/layout)",
        ["FAIL"] = "probe infrastructure failure or assertion violated",
    };

    private static readonly List<ScenarioResult> Results = [];
    private static ImeProbeWindow? _main;
    private static ImeProbeWindow? _focusTarget;
    private static TsfProfile.TemporaryProfileScope? _temporaryProfile;
    private static bool _profileRestoreRecorded;
    private static bool _cleanedUp;
    private static bool _closeRequested;
    private static long _deadlineTicks;
    private static bool _timedOut;

    [STAThread]
    private static int Main(string[] args)
    {
        var options = ProbeOptions.Parse(args);
        if (!NativeMethods.IsWindowsHost())
        {
            Console.Error.WriteLine("CSharp.UI.ImeProbe: this probe only runs on Windows.");
            return 3;
        }

        Directory.CreateDirectory(options.OutDirectory);
        using var log = new ProbeLog(Path.Combine(options.OutDirectory, "events.jsonl"));

        // 身份核对的目标由启动参数决定，而不是写死微软拼音：
        // --temporary-enable-mspinyin on 或 --profile mspinyin -> 微软拼音；--profile wetype -> 微信输入法；
        // --profile current -> 本进程启动时实际活动的那一个（含键盘布局）。
        IdentityProbe.Target = ResolveTargetProfile(log, options);
        var summaryPath = Path.Combine(options.OutDirectory, "summary.json");
        var summaryTextPath = Path.Combine(options.OutDirectory, "summary.txt");

        // OpenTK 4.9.4 的默认 GLFW 错误回调会直接抛异常，连手柄/输入接口这类良性失败也会中断窗口创建。
        // 探针改为“记录而不抛出”，窗口/上下文创建失败仍由空句柄检查显式报告。
        var glfwErrorCount = 0;
        GLFWProvider.SetErrorCallback((code, description) =>
        {
            Interlocked.Increment(ref glfwErrorCount);
            log.Record("glfw", "glfw-error-callback", "glfw-error", new Dictionary<string, object?>
            {
                ["code"] = code.ToString(),
                ["description"] = description,
            });
        });

        _deadlineTicks = Environment.TickCount64 + (long)(options.DurationSeconds * 1000);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("Ctrl+C: restoring subclass and exiting.");
            Cleanup();
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        try
        {
            log.Record("probe", "ProbeLog", "options", new Dictionary<string, object?>
            {
                ["scenario"] = options.Scenario,
                ["profile"] = options.Profile,
                ["keys"] = options.Keys,
                ["out"] = options.OutDirectory,
                ["capture"] = options.Capture,
                ["duration_seconds"] = options.DurationSeconds,
                ["inter_key_delay_ms"] = options.InterKeyDelayMs,
                ["settle_ms"] = options.SettleMs,
                ["argv"] = string.Join(" ", args),
            });

            var comInitialized = false;
            // TSF 要求线程是 STA：探针入口带 [STAThread]，这里显式初始化并记录真实 apartment，
            // 避免把“MTA 下激活失败”误判成输入法不可用（见 docs/return 审核记录）。
            var apartmentHr = TsfProfile.InitializeCom();
            var apartmentType = -1;
            var apartmentQualifier = -1;
            var apartmentQueryHr = NativeMethods.CoGetApartmentType(out apartmentType, out apartmentQualifier);
            log.Record("tsf", "ole32-api", "apartment", new Dictionary<string, object?>
            {
                ["coinitialize_ex_hresult"] = $"0x{apartmentHr:X8}",
                ["coinitialize_succeeded_or_already"] = apartmentHr >= 0,
                ["coinitialize_s_false_means_already_sta"] = apartmentHr == 1,
                ["com_initialized_by_probe"] = apartmentHr == 0,
                ["co_get_apartment_type_hresult"] = $"0x{apartmentQueryHr:X8}",
                ["apartment_type"] = ApartmentTypeName(apartmentType),
                ["apartment_qualifier"] = apartmentQualifier,
                ["entry_attribute"] = "[STAThread] on Main",
                ["win32_thread_id"] = NativeMethods.CurrentWin32ThreadId(),
            });
            comInitialized = apartmentHr >= 0;

            _main = new ImeProbeWindow(
                log,
                "main",
                "TI-01 IME probe (dedicated test window)",
                options.Width,
                options.Height,
                visible: true,
                subclass: true,
                captureDirectory: options.Capture ? Path.Combine(options.OutDirectory, "screenshots") : null);

            log.Record("probe", "probe-window", "environment", BuildEnvironmentReport(_main));

            // 关闭请求只做标记：窗口销毁仍然由 Cleanup/Dispose 在消息路径可观察时执行。
            _main.Window.Closing += _ =>
            {
                _closeRequested = true;
                log.Record("glfw", "glfw-close-callback", "close-requested", new Dictionary<string, object?>
                {
                    ["composing"] = _main.IsComposing,
                    ["window_exists"] = _main.Window.Exists,
                });
            };

            if (options.Scenario is "ime-focus" or "all")
            {
                _focusTarget = new ImeProbeWindow(
                    log,
                    "focus-target",
                    "TI-01 focus target (own window, no subclass logging)",
                    420,
                    240,
                    visible: true,
                    subclass: false,
                    captureDirectory: null);
            }

            var script = new InputScript(log, _main.Handle, "main");
            if (options.InputMode is "pumped" or "scancode")
            {
                // 逐键之间也泵消息：用于对照 TSF 输入法在消息循环保持活着时是否处理注入键。
                script.PumpHook = Pump;
            }

            script.UseScanCodes = options.UseScanCodes;

            log.Record("script", "sendinput", "input-mode", new Dictionary<string, object?>
            {
                ["mode"] = options.InputMode,
                ["pumped_between_keys"] = options.InputMode is "pumped" or "scancode",
                ["real_scan_codes"] = options.UseScanCodes,
                ["note"] = options.InputMode switch
                {
                    "pumped" => "messages are pumped between every injected key down/up (virtual keys, wScan=0)",
                    "scancode" => "messages pumped between keys; real scan codes via KEYEVENTF_SCANCODE, no WM_IME message is synthesized",
                    _ => "SendInput + Sleep per key, then all messages pumped after the burst",
                },
            });

            if (options.AssociateOwnContext)
            {
                _main.AssociateOwnImeContext();
            }

            if (options.EngageTsf)
            {
                TsfSession.Engage(log, _main.Handle, "main");
                _main.LogCurrentImeState("after-tsf-engage");
            }

            if (options.FaultInject is "message" or "log")
            {
                _main.EnableFaultInjection(options.FaultInject, NativeMethods.WM_CHAR);
            }

            _main.CaptureImeState();
            _main.LogCurrentImeState("probe-start");

            // 初始化顺序（本轮的对照实验）：先获焦并泵，让窗口消息/焦点真正落地；再激活临时 profile；
            // 再泵，使 activation 与新的输入法上下文到达本线程；最后才针对“实际目标 HIMC”设置 native/open
            // 并再次泵+回读。
            //
            // 假设（尚未证实，勿写成根因）：上一轮的 SetNativeImeMode 发生在 profile 激活<b>之前</b>，
            // 可能针对旧上下文生效、随后被异步的 profile/上下文切换覆盖。必须靠本轮前后对照数据判断，
            // 不能只凭顺序改动就宣称根因已证实。
            var foreground = script.EnsureForeground();
            Pump(1200);
            TsfProfile.ReadActiveProfile(log, "after-focus-before-activation");
            IdentityProbe.Record(log, _main, "after-focus-before-activation");
            _main.LogCurrentImeState("after-focus-before-activation");

            log.Record("probe", "initialization-order", "profile-activation-order", new Dictionary<string, object?>
            {
                ["step"] = "focus+pump -> profile activation -> pump -> set-native-mode(target HIMC) -> pump+readback",
                ["foreground_confirmed"] = foreground,
                ["previous_order"] = "set-native-mode happened BEFORE activation; that is the hypothesis under test, not a proven root cause",
                ["hypothesis"] = "the pre-activation ImmSetOpenStatus/ImmSetConversionStatus may have been applied to a context "
                    + "that the later profile activation replaced; only the before/after readbacks in this run can test it",
            });

            // 目标输入窗口创建并获得焦点之后，再确认“实际活动 profile”，而不是把请求参数当身份。
            if (options.TemporaryEnableMsPinyin)
            {
                var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
                    log,
                    "microsoft-pinyin-temporary",
                    TsfProfile.MicrosoftPinyinClsid,
                    TsfProfile.MicrosoftPinyinProfile,
                    TsfProfile.LangIdChineseSimplified);
                _temporaryProfile = attempt.Scope;
                if (!attempt.Activated)
                {
                    // 不能无条件声称“所有更改已恢复”：只按实际回读结果报告。
                    Results.Add(new ScenarioResult("profile-temporary-enable", "FAIL",
                        "explicit temporary Microsoft Pinyin enable was requested but was not verified; "
                        + $"configuration_touched={attempt.ConfigurationTouched}, restore_readback_verified={attempt.RestoreVerified}, "
                        + $"detail={attempt.Failure}",
                        new Dictionary<string, object?>
                        {
                            ["configuration_touched"] = attempt.ConfigurationTouched,
                            ["restore_readback_verified"] = attempt.RestoreVerified,
                            ["failure"] = attempt.Failure,
                        }));
                }
            }
            else if (options.Profile is "mspinyin" or "wetype")
            {
                var (clsid, profile) = options.Profile == "mspinyin"
                    ? (TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile)
                    : (TsfProfile.WeTypeClsid, TsfProfile.WeTypeProfile);
                TsfProfile.ActivateForProcess(log, $"profile-{options.Profile}", clsid, profile, TsfProfile.LangIdChineseSimplified);
            }

            // 激活之后再泵：让 activation/新上下文在被设置之前真正到达本线程。
            Pump(600);
            TsfProfile.ReadActiveProfile(log, "after-activation-before-set-native");
            IdentityProbe.Record(log, _main, "after-activation-before-set-native");
            _main.LogCurrentImeState("after-activation-before-set-native");

            if (options.ImeMode == "native")
            {
                // 只针对此刻实际的目标 HIMC 设置；结果含 IMM 回读值，由调用方按回读判定，不把调用当成成功。
                var nativeMode = _main.SetNativeImeMode();
                Pump(300);
                _main.LogCurrentImeState("after-set-native-mode");
                Results.Add(new ScenarioResult(
                    "native-mode-readback",
                    nativeMode.Applied ? "PASS" : "UNVERIFIED",
                    nativeMode.Applied
                        ? "ImmSetOpenStatus(true) and CMODE_NATIVE|CMODE_ROMAN were read back from the actual target HIMC "
                            + $"({nativeMode.Himc}) after the profile activation had landed"
                        : "the native mode request did NOT read back from the target HIMC: " + nativeMode.Reason
                            + " (this is recorded, not silently treated as success)",
                    new Dictionary<string, object?>
                    {
                        ["himc"] = nativeMode.Himc,
                        ["applied"] = nativeMode.Applied,
                        ["open_applied"] = nativeMode.OpenApplied,
                        ["conversion_applied"] = nativeMode.ConversionApplied,
                        ["readback_native"] = nativeMode.ReadbackNative,
                        ["reason"] = nativeMode.Reason,
                        ["order"] = "focus+pump -> profile activation -> pump -> set native/open on target HIMC -> pump+readback",
                    }));
            }

            TsfProfile.ReadActiveProfile(log, "after-focus");
            IdentityProbe.Record(log, _main, "after-focus");
            _main.LogCurrentImeState("after-focus");

            RunScenarios(options, log, script);

            RestoreTemporaryProfile();

            if (comInitialized)
            {
                // 恢复用户原有配置由调用方在外部核对；这里只把进程内配置切回运行前记录的默认项（若已知）。
                TsfProfile.UninitializeCom();
            }

            if (TsfSession.IsActive)
            {
                var releaseHr = TsfSession.Release();
                log.Record("tsf", "msctf-api", "release-thread-mgr", new Dictionary<string, object?>
                {
                    ["hresult"] = $"0x{releaseHr:X8}",
                });
            }
        }
        catch (Exception ex)
        {
            log.Record("probe", "ProbeLog", "fatal", new Dictionary<string, object?>
            {
                ["exception"] = ex.ToString(),
            });
            Results.Add(new ScenarioResult("infrastructure", "FAIL", $"{ex.GetType().Name}: {ex.Message}", []));
        }
        finally
        {
            Cleanup();
            WriteSummary(log, summaryPath, summaryTextPath);
        }

        var failed = Results.Any(r => r.Status == "FAIL");
        var exit = failed ? 2 : 0;
        Console.WriteLine($"probe finished: results={Results.Count} failed={Results.Count(r => r.Status == "FAIL")} exit={exit}");
        return exit;
    }

    private static void RunScenarios(ProbeOptions options, ProbeLog log, InputScript script)
    {
        switch (options.Scenario)
        {
            case "info":
                Results.Add(new ScenarioResult("info", "PASS", "environment snapshot only", []));
                break;
            case "ime-commit":
                RunAndAdd(() => RunImeCommit(options, log, script));
                break;
            case "ime-segmented":
                RunAndAdd(() => RunImeSegmented(options, log, script));
                break;
            case "ime-cancel":
                RunAndAdd(() => RunImeCancel(options, log, script));
                break;
            case "ime-focus":
                RunAndAdd(() => RunImeFocus(options, log, script));
                break;
            case "ime-resize":
                RunAndAdd(() => RunImeResize(options, log, script));
                break;
            case "ime-candidate":
                RunAndAdd(() => RunImeCandidate(options, log, script));
                break;
            case "latin":
                RunAndAdd(() => RunLatin(options, log, script));
                break;
            case "repeat":
                RunAndAdd(() => RunRepeatPath(options, log, script));
                break;
            case "nonbmp":
                RunAndAdd(() => RunNonBmp(options, log, script));
                break;
            case "fault":
                RunAndAdd(() => RunFaultInjection(options, log, script));
                break;
            case "fault-restore":
                RunAndAdd(() => RunFaultRestore(options, log));
                break;
            case "ime-close":
                RunAndAdd(() => RunImeClose(options, log, script));
                break;
            case "manual":
                RunAndAdd(() => RunManual(options, log));
                break;
            case "all":
                RunAndAdd(() => RunImeCommit(options, log, script));
                RunAndAdd(() => RunImeSegmented(options, log, script));
                RunAndAdd(() => RunImeCancel(options, log, script));
                RunAndAdd(() => RunImeFocus(options, log, script));
                RunAndAdd(() => RunImeCandidate(options, log, script));
                RunAndAdd(() => RunImeResize(options, log, script));
                RunAndAdd(() => RunLatin(options, log, script));
                RunAndAdd(() => RunRepeatPath(options, log, script));
                RunAndAdd(() => RunNonBmp(options, log, script));
                RunAndAdd(() => RunFaultInjection(options, log, script));
                RunAndAdd(() => RunImeClose(options, log, script));
                break;
            default:
                Results.Add(new ScenarioResult(options.Scenario, "FAIL", "unknown scenario", []));
                break;
        }
    }

    /// <summary>
    /// 运行一个场景并记录结果；若该场景期间出现非预期的消息泵/渲染异常，PASS 降级为 UNVERIFIED，
    /// 因为“渲染失败时仍判通过”会掩盖探针基础设施故障。
    /// </summary>
    private static void RunAndAdd(Func<ScenarioResult> run)
    {
        var before = _main?.UnexpectedPumpExceptionCount ?? 0;
        var result = run();
        var after = _main?.UnexpectedPumpExceptionCount ?? 0;
        if (after > before && result.Status == "PASS")
        {
            result = result with
            {
                Status = "UNVERIFIED",
                Notes = result.Notes + $" [downgraded from PASS: {after - before} unexpected event-pump/render exception(s); see events.jsonl]",
            };
        }

        Results.Add(result);
    }

    /// <summary>
    /// 解析本次运行的输入法目标用于身份核对。只读：<c>current</c> 需要查一次当前活动 profile，
    /// 查询失败就如实返回 null，不假装有一个可核对的目标。
    /// </summary>
    private static ProbeTargetProfile? ResolveTargetProfile(ProbeLog log, ProbeOptions options)
    {
        if (options.TemporaryEnableMsPinyin || options.Profile == "mspinyin")
        {
            return ProbeTargetProfile.MicrosoftPinyin;
        }

        if (options.Profile == "wetype")
        {
            return ProbeTargetProfile.WeType;
        }

        var (hr, active) = TsfProfile.ReadActiveProfile(log, "identity-target:current");
        if (hr < 0)
        {
            log.Record("tsf", "msctf-api", "identity-target-unavailable", new Dictionary<string, object?>
            {
                ["hresult"] = $"0x{hr:X8}",
                ["note"] = "GetActiveProfile failed, so there is no comparable identity target for --profile current",
            });
            return null;
        }

        return new ProbeTargetProfile(
            "current-active-profile", active.ProfileType, active.LangId, active.Clsid, active.ProfileGuid);
    }

    private static ScenarioResult RunImeCommit(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "ime-commit", out var reason))
        {
            return new ScenarioResult("ime-commit", "SKIP", reason, []);
        }

        // 场景起点快照放在 Prepare 之后，避免 Prepare 期间的消息/焦点变化污染本场景计数。
        var before = new Observation(main);

        main.CaptureScreenshot("ime-commit-before-typing", 120);
        if (!RunWarmup(options, log, script))
        {
            return Aborted("ime-commit", script);
        }
        if (!script.SendAsciiText(options.Keys, "ime-commit"))
        {
            return Aborted("ime-commit", script);
        }

        Pump(options.SettleMs);
        main.LogCurrentImeState("after-typing");
        main.RequestCandidateAnchor("script-after-typing");
        main.CaptureScreenshot("ime-commit-candidate-visible", 250);

        if (!script.PressVirtualKey(0x20, "ime-commit:space-commit"))
        {
            return Aborted("ime-commit", script);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("ime-commit-after-commit", 250);
        var after = new Observation(main);

        var newSegments = after.ResultSegments.Skip(before.ResultSegments.Count).ToList();
        var newResultFlags = after.ResultFlagCommitCount - before.ResultFlagCommitCount;
        var newTextInputs = after.TextInputs.Skip(before.TextInputs.Count).ToList();
        var textChannel = string.Concat(newTextInputs);
        var duplicateChannelSameText = newSegments.Count == 1 && textChannel.Length > 0 && textChannel == newSegments[0];

        // “段数恰好为 1”只证明计数正确；必须再核对提交内容本身，并且必须能归因到本次组合会话的预编辑。
        // requireChinese 只在明确走中文输入法（mspinyin/wetype）时为 true，美国键盘路径不误判。
        var requireChinese = options.Profile is "mspinyin" or "wetype" || options.TemporaryEnableMsPinyin;
        var expected = CommitOutcome.ExpectedResultFor(options.Keys);
        // 用本结果自己冻结的快照（含它所属组合会话的预编辑历史），而不是“最后一段会话”的历史。
        var newSnapshots = CommitSnapshotSlicer.TakeNewResults(after.CommitSnapshots, before.CommitSnapshots.Count);
        var verdict = newSnapshots.Count == 1
            ? CommitSnapshotSlicer.EvaluateSnapshot(newSnapshots[0], expected, requireChinese, options.Keys)
            : new CommitContentVerdict(false, false, false, "no single new result segment to evaluate");
        var identityMatched = RecordScenarioEndIdentity(main, log, "ime-commit");

        var details = new Dictionary<string, object?>
        {
            ["keys"] = options.Keys,
            ["commit_key"] = "VK_SPACE (0x20)",
            ["composition_updates"] = after.CompositionUpdateCount - before.CompositionUpdateCount,
            ["result_segments_total"] = after.ResultSegments.Count,
            ["result_segments_new"] = newSegments.Select(ImeFormat.EscapeUtf16).ToList(),
            ["result_segments_new_text"] = newSegments,
            ["result_flag_commits"] = newResultFlags,
            ["snapshot_only_result_reads"] = after.SnapshotOnlyResultCount - before.SnapshotOnlyResultCount,
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["text_inputs_new"] = newTextInputs,
            ["ime_char_messages"] = after.ImeCharMessageCount - before.ImeCharMessageCount,
            ["char_messages"] = after.CharMessageCount - before.CharMessageCount,
            ["duplicate_result_and_char_channel_same_text"] = duplicateChannelSameText,
            ["imm_get_description"] = after.CurrentImeDescription,
            ["ime_file_name"] = after.CurrentImeFileName,
            ["candidate_requests"] = after.CandidateRequestCount - before.CandidateRequestCount,
            ["commit_requires_chinese"] = requireChinese,
            ["commit_expected_result"] = expected ?? "(no fixed expected content; non-ASCII + attribution only)",
            ["commit_content_ok"] = verdict.Satisfied,
            ["commit_pinyin_seen_in_session_preedit"] = verdict.PinyinSeenInPreedit,
            ["commit_content_matched"] = verdict.ContentMatched,
            ["commit_snapshot_composition_id"] = newSnapshots.Count == 1 ? newSnapshots[0].CompositionId : -1,
            ["commit_snapshot_preedit_history"] = newSnapshots.Count == 1
                ? newSnapshots[0].PreeditHistory.ToList()
                : [],
            ["commit_content_reason"] = verdict.Reason,
            ["identity_snapshots"] = IdentityProbe.SnapshotCount,
            ["actual_active_matches_configured_profile_at_end"] = identityMatched,
            ["assertion"] = "exactly one new GCS_RESULTSTR-flagged commit segment for the typed pinyin, "
                + "that segment's committed text equals the expected content when a fixed test string was used, "
                + "and the injected pinyin was observed in that segment's own composition preedit history",
        };

        string status;
        string notes;
        if (newSegments.Count > 1 || newResultFlags > 1)
        {
            status = "FAIL";
            notes = "more than one result/increment for a single commit: commit counting or channel ownership is wrong";
        }
        else if (newSegments.Count != 1 || newResultFlags != 1)
        {
            status = "UNVERIFIED";
            notes = after.CompositionUpdateCount > before.CompositionUpdateCount
                ? "composition updated but no GCS_RESULTSTR-flagged result arrived; the commit key did not produce a result in this run"
                : "no WM_IME composition started; the real IME did not engage in this run";
        }
        else if (!verdict.Satisfied)
        {
            // 关键区分：确实收到了一次结果，但内容不是预期的中文选词（例如失焦回吐的 "niha"）。
            status = "UNVERIFIED";
            notes = "exactly one flagged result commit arrived, but the committed content does not satisfy the "
                + $"expected Chinese selection: {verdict.Reason}";
        }
        else
        {
            status = "PASS";
            notes = (duplicateChannelSameText
                ? "one flagged result commit observed; the same text also arrived through WM_IME_CHAR/WM_CHAR -> TextInput, so the host must apply exactly one channel"
                : "one flagged result commit observed (no duplicate char-channel text in this run)")
                + $"; committed text verdict: {verdict.Reason}";
        }

        return new ScenarioResult("ime-commit", status, notes, details);
    }

    private static ScenarioResult RunImeSegmented(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "ime-segmented", out var reason))
        {
            return new ScenarioResult("ime-segmented", "SKIP", reason, []);
        }

        var before = new Observation(main);

        // 第一段：提交一次结果；随后继续输入第二段，检查“结果 + 后续预编辑”是否出现在同一消息或分段出现。
        if (!script.SendAsciiText(options.Keys, "ime-segmented:first")
            || !script.PressVirtualKey(0x20, "ime-segmented:first-commit"))
        {
            return Aborted("ime-segmented", script);
        }

        Pump(options.SettleMs);
        if (!script.SendAsciiText(options.SecondKeys, "ime-segmented:second")
            || !script.PressVirtualKey(0x20, "ime-segmented:second-commit"))
        {
            return Aborted("ime-segmented", script);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("ime-segmented-done", 200);
        var after = new Observation(main);

        var combined = after.ResultAndPreeditSameMessage;
        var newSegments = after.ResultSegments.Skip(before.ResultSegments.Count).ToList();
        var newFlags = after.ResultFlagCommitCount - before.ResultFlagCommitCount;
        var requireChinese = options.Profile is "mspinyin" or "wetype" || options.TemporaryEnableMsPinyin;
        // 两段各自核对内容：第一段对应 keys，第二段对应 second-keys。
        // 每段使用**该段结果自己冻结的**compositionId 与预编辑历史快照：
        // 第二段的新组合会话会清空当前历史，用“最后一段历史”判定第一段必然失败。
        var firstExpected = CommitOutcome.ExpectedResultFor(options.Keys);
        var secondExpected = CommitOutcome.ExpectedResultFor(options.SecondKeys);
        var newSnapshots = CommitSnapshotSlicer.TakeNewResults(after.CommitSnapshots, before.CommitSnapshots.Count);
        var firstVerdict = CommitSnapshotSlicer.EvaluateFirstNew(
            newSnapshots, firstExpected, requireChinese, options.Keys);
        var secondVerdict = CommitSnapshotSlicer.EvaluateSecondNew(
            newSnapshots, secondExpected, requireChinese, options.SecondKeys);
        var identityMatched = RecordScenarioEndIdentity(main, log, "ime-segmented");

        var details = new Dictionary<string, object?>
        {
            ["first_keys"] = options.Keys,
            ["second_keys"] = options.SecondKeys,
            ["result_segments_total"] = after.ResultSegments.Count,
            ["result_segments_new"] = newSegments.Select(ImeFormat.EscapeUtf16).ToList(),
            ["result_segments_new_text"] = newSegments,
            ["result_flag_commits"] = newFlags,
            ["snapshot_only_result_reads"] = after.SnapshotOnlyResultCount - before.SnapshotOnlyResultCount,
            ["result_and_preedit_same_message"] = combined,
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["commit_requires_chinese"] = requireChinese,
            ["first_expected_result"] = firstExpected ?? "(non-ASCII + attribution only)",
            ["second_expected_result"] = secondExpected ?? "(non-ASCII + attribution only)",
            ["first_commit_pinyin_seen_in_session_preedit"] = firstVerdict.PinyinSeenInPreedit,
            ["second_commit_pinyin_seen_in_session_preedit"] = secondVerdict.PinyinSeenInPreedit,
            ["first_commit_snapshot_composition_id"] = newSnapshots.Count >= 1 ? newSnapshots[0].CompositionId : -1,
            ["first_commit_snapshot_preedit_history"] = newSnapshots.Count >= 1
                ? newSnapshots[0].PreeditHistory.ToList()
                : [],
            ["second_commit_snapshot_composition_id"] = newSnapshots.Count >= 2 ? newSnapshots[1].CompositionId : -1,
            ["second_commit_snapshot_preedit_history"] = newSnapshots.Count >= 2
                ? newSnapshots[1].PreeditHistory.ToList()
                : [],
            ["first_commit_content_ok"] = firstVerdict.Satisfied,
            ["first_commit_content_reason"] = firstVerdict.Reason,
            ["second_commit_content_ok"] = secondVerdict.Satisfied,
            ["second_commit_content_reason"] = secondVerdict.Reason,
            ["identity_snapshots"] = IdentityProbe.SnapshotCount,
            ["actual_active_matches_configured_profile_at_end"] = identityMatched,
            ["assertion"] = "each of the two commits appears exactly once, carries the GCS_RESULTSTR flag, "
                + "and (when a Chinese IME was configured and a fixed test string was used) each committed text equals "
                + "its expected content and the injected pinyin was observed in that commit's own composition preedit history",
        };

        string status;
        string notes;
        if (newFlags > newSegments.Count)
        {
            status = "FAIL";
            notes = "more flagged commits than result segments: counting is inconsistent";
        }
        else if (newSegments.Count != 2 || newFlags != 2)
        {
            status = "UNVERIFIED";
            notes = $"segmented commits observed={newSegments.Count} (expected 2); result+preedit same message={combined}";
        }
        else if (!firstVerdict.Satisfied || !secondVerdict.Satisfied)
        {
            status = "UNVERIFIED";
            notes = "two flagged commits arrived, but their content does not satisfy the expected Chinese selection: "
                + $"first=[{firstVerdict.Reason}]; second=[{secondVerdict.Reason}]";
        }
        else
        {
            status = "PASS";
            notes = $"two flagged result commits observed; result+preedit in one WM_IME_COMPOSITION observed={combined} "
                + "(TI-04 must still handle both shapes); each commit text equals its expected content and the injected "
                + "pinyin was observed in that commit's own composition preedit history";
        }

        return new ScenarioResult("ime-segmented", status, notes, details);
    }

    private static ScenarioResult RunImeCancel(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "ime-cancel", out var reason))
        {
            return new ScenarioResult("ime-cancel", "SKIP", reason, []);
        }

        var before = new Observation(main);

        if (!script.SendAsciiText(options.Keys, "ime-cancel"))
        {
            return Aborted("ime-cancel", script);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("ime-cancel-before-escape", 200);
        if (!script.PressVirtualKey(0x1B, "ime-cancel:escape"))
        {
            return Aborted("ime-cancel", script);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("ime-cancel-after-escape", 200);
        var after = new Observation(main);

        var compositionStarted = after.CompositionUpdateCount - before.CompositionUpdateCount > 0;
        var endedWithoutResult = after.Cancellations > before.Cancellations;
        var noResult = after.ResultSegments.Count == before.ResultSegments.Count;
        var windowAlive = main.Window.Exists;
        var identityMatched = RecordScenarioEndIdentity(main, log, "ime-cancel");
        var details = new Dictionary<string, object?>
        {
            ["composition_updates"] = after.CompositionUpdateCount - before.CompositionUpdateCount,
            ["ended_without_result"] = endedWithoutResult,
            ["result_segments_unchanged"] = noResult,
            ["window_alive_after_escape"] = windowAlive,
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["identity_snapshots"] = IdentityProbe.SnapshotCount,
            ["actual_active_matches_configured_profile_at_end"] = identityMatched,
            ["assertion"] = "composition started, Escape ended it without a result, no result segment was added, window survived",
        };

        string status;
        string notes;
        if (compositionStarted && endedWithoutResult && noResult && windowAlive)
        {
            status = "PASS";
            notes = "Escape cancelled the real composition without commit and without reaching the host exit shortcut";
        }
        else if (!compositionStarted)
        {
            status = "UNVERIFIED";
            notes = "no WM_IME composition was observed before Escape";
        }
        else if (!noResult)
        {
            status = "FAIL";
            notes = "a result segment was recorded during a cancel scenario";
        }
        else
        {
            status = "UNVERIFIED";
            notes = "composition started but no cancellation was observed after Escape";
        }

        return new ScenarioResult("ime-cancel", status, notes, details);
    }

    private static ScenarioResult RunImeFocus(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        var target = _focusTarget;
        if (target is null)
        {
            return new ScenarioResult("ime-focus", "SKIP", "focus target window was not created", []);
        }

        if (!Prepare(options, main, script, log, "ime-focus", out var reason))
        {
            return new ScenarioResult("ime-focus", "SKIP", reason, []);
        }

        var before = new Observation(main);

        if (!script.SendAsciiText(options.Keys, "ime-focus"))
        {
            return Aborted("ime-focus", script);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("ime-focus-composing", 200);

        NativeMethods.SetForegroundWindow(target.Handle);
        Pump(900);
        var afterLoss = new Observation(main);
        main.CaptureScreenshot("ime-focus-lost", 200);

        NativeMethods.SetForegroundWindow(main.Handle);
        Pump(600);
        main.CaptureScreenshot("ime-focus-regained", 200);
        var after = new Observation(main);

        var compositionStarted = after.CompositionUpdateCount - before.CompositionUpdateCount > 0;
        var cancelledOnFocusLoss = afterLoss.Cancellations > before.Cancellations;
        var windowAlive = main.Window.Exists;
        // 真实证据显示：失焦时某些输入法会“自动提交未转换的预编辑串”而不是取消（例如 GCS_RESULTSTR="niha"）。
        // 这是必须记录的另一种真实形态，不能当成取消，也不能当成中文选词提交。
        var autoCommitOnFocusLoss = afterLoss.ResultFlagCommitCount - before.ResultFlagCommitCount;
        var autoCommittedSegments = afterLoss.ResultSegments.Skip(before.ResultSegments.Count).ToList();
        var identityMatched = RecordScenarioEndIdentity(main, log, "ime-focus");
        var details = new Dictionary<string, object?>
        {
            ["composition_updates"] = after.CompositionUpdateCount - before.CompositionUpdateCount,
            ["cancellations_after_focus_loss"] = afterLoss.Cancellations - before.Cancellations,
            ["ended_without_result_on_focus_loss"] = cancelledOnFocusLoss,
            ["auto_commit_on_focus_loss"] = autoCommitOnFocusLoss,
            ["auto_committed_segments"] = autoCommittedSegments.Select(ImeFormat.EscapeUtf16).ToList(),
            ["auto_committed_text"] = autoCommittedSegments,
            ["auto_committed_text_is_ascii_only"] = autoCommittedSegments.Count > 0 && autoCommittedSegments.All(CommitOutcome.IsAsciiOnly),
            ["window_alive_after_focus_round_trip"] = windowAlive,
            ["focus_target_is_own_window"] = true,
            ["identity_snapshots"] = IdentityProbe.SnapshotCount,
            ["actual_active_matches_configured_profile_at_end"] = identityMatched,
            ["assertion"] = "focus loss to our own second window ends the composition; EITHER without a result (cancel) "
                + "OR by auto-committing the un-converted preedit, which is recorded as a distinct, non-PASS shape",
        };

        string status;
        string notes;
        if (!compositionStarted)
        {
            status = "UNVERIFIED";
            notes = "no WM_IME composition was observed before switching focus";
        }
        else if (cancelledOnFocusLoss && windowAlive)
        {
            status = "PASS";
            notes = "focus loss cancelled the composition without commit; focus was then restored to the same window";
        }
        else if (autoCommitOnFocusLoss > 0)
        {
            status = "OBSERVED";
            notes = $"focus loss AUTO-COMMITTED the composition instead of cancelling it "
                + $"({autoCommitOnFocusLoss} result segment(s): {string.Join("|", autoCommittedSegments.Select(ImeFormat.EscapeUtf16))}). "
                + "An auto-commit of the un-converted preedit is NOT a Chinese candidate selection and is NOT a verified cancel; "
                + "the TI-04 state machine must still handle this shape";
        }
        else
        {
            status = "UNVERIFIED";
            notes = "composition started but no cancellation and no auto-commit was observed after focus loss";
        }

        return new ScenarioResult("ime-focus", status, notes, details);
    }

    private static ScenarioResult RunImeResize(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "ime-resize", out var reason))
        {
            return new ScenarioResult("ime-resize", "SKIP", reason, []);
        }

        var before = new Observation(main);

        if (!script.SendAsciiText(options.Keys, "ime-resize"))
        {
            return Aborted("ime-resize", script);
        }

        Pump(options.SettleMs);
        main.RequestCandidateAnchor("before-resize");
        main.CaptureScreenshot("ime-resize-before", 200);

        var originalSize = main.Window.ClientSize;
        var newSize = new global::OpenTK.Mathematics.Vector2i(originalSize.X + 320, originalSize.Y + 160);
        main.Window.ClientSize = newSize;
        Pump(options.SettleMs);
        main.RequestCandidateAnchor("after-resize");
        main.CaptureScreenshot("ime-resize-after-resize", 250);

        var originalLocation = main.Window.Location;
        main.Window.Location = new global::OpenTK.Mathematics.Vector2i(originalLocation.X + 60, originalLocation.Y + 40);
        Pump(options.SettleMs);
        main.RequestCandidateAnchor("after-move");
        main.CaptureScreenshot("ime-resize-after-move", 250);

        main.Window.ClientSize = originalSize;
        Pump(400);

        var after = new Observation(main);
        var anchorRequests = after.CandidateRequestCount - before.CandidateRequestCount;
        var anchorSetOk = after.AnchorSetOkCount - before.AnchorSetOkCount;
        var anchorGetOk = after.AnchorGetOkCount - before.AnchorGetOkCount;
        var details = new Dictionary<string, object?>
        {
            ["original_client"] = $"{originalSize.X}x{originalSize.Y}",
            ["resized_client"] = $"{newSize.X}x{newSize.Y}",
            ["candidate_requests"] = anchorRequests,
            ["candidate_set_ok"] = anchorSetOk,
            ["candidate_get_ok"] = anchorGetOk,
            ["composition_updates"] = after.CompositionUpdateCount - before.CompositionUpdateCount,
            ["composing_at_end"] = after.IsComposing,
            ["system_candidate_actual_position_confirmed"] = false,
            ["assertion"] = "three anchor publishes across resize/move all returned success from ImmSetCandidateWindow and ImmGetCandidateWindow",
        };
        // 该场景只证明锚点 API 往返成功；系统候选窗的实际位置没有独立观测，因此不能作为“候选定位”通过。
        var apiRoundTrip = anchorRequests >= 3 && anchorSetOk >= 3 && anchorGetOk >= 3;
        var status = apiRoundTrip ? "OBSERVED" : "FAIL";
        var notes = status == "OBSERVED"
            ? "anchor API round-trip succeeded for each publish point, but the actual on-screen system candidate window position was not independently observed (candidate localization remains UNVERIFIED)"
            : "anchor publish/readback did not succeed for every publish point";
        return new ScenarioResult("ime-resize", status, notes, details);
    }

    /// <summary>
    /// 候选窗锚点场景：在自有窗口<b>内部</b>两个不同的客户区坐标各发布一次候选锚点并各截一张自有窗口矩形截图，
    /// 用于人工核对系统候选是否可见、是否随锚点移动。
    /// </summary>
    /// <remarks>
    /// 本场景只做两件事：(1) 用 ImmSetCandidateWindow/ImmSetCompositionWindow 发布锚点并读回 IME 报告值；
    /// (2) 在窗口矩形完全一致的前提下各截一张图。它<b>不</b>枚举或抓取任何非自有窗口，也不扩大截图 margin。
    /// 因此：只要没有对截图的独立核对，这里最高只能是 OBSERVED/UNVERIFIED，
    /// 绝不用“锚点 API 往返成功”冒充“候选实际定位成功”。
    /// </remarks>
    private static ScenarioResult RunImeCandidate(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "ime-candidate", out var reason))
        {
            return new ScenarioResult("ime-candidate", "SKIP", reason, []);
        }

        if (!options.Capture)
        {
            return new ScenarioResult("ime-candidate", "SKIP",
                "candidate position verification needs the own-window screenshots (--capture on); nothing was faked", []);
        }

        var before = new Observation(main);
        if (!script.SendAsciiText(options.Keys, "ime-candidate"))
        {
            return Aborted("ime-candidate", script);
        }

        Pump(options.SettleMs);
        main.LogCurrentImeState("ime-candidate-after-typing");
        var candidates = main.CurrentCandidateCount;
        var composing = main.IsComposing;

        var pointA = main.InWindowAnchorPoint(0);
        main.RequestCandidateAnchorAtPoint("ime-candidate-anchor-a", pointA);
        var rectA = main.CurrentWindowRect();
        main.CaptureScreenshot("ime-candidate-anchor-a", 250);
        var rectAAfter = main.CurrentWindowRect();

        var pointB = main.InWindowAnchorPoint(1);
        main.RequestCandidateAnchorAtPoint("ime-candidate-anchor-b", pointB);
        var rectB = main.CurrentWindowRect();
        main.CaptureScreenshot("ime-candidate-anchor-b", 250);
        var rectBAfter = main.CurrentWindowRect();

        // 两端都不要留下组合：Esc 取消，避免把未提交状态带出场景。
        script.PressVirtualKey(0x1B, "ime-candidate:escape");
        Pump(options.SettleMs);

        var after = new Observation(main);
        var anchorRequests = after.CandidateRequestCount - before.CandidateRequestCount;
        var anchorSetOk = after.AnchorSetOkCount - before.AnchorSetOkCount;
        var anchorGetOk = after.AnchorGetOkCount - before.AnchorGetOkCount;
        var sameWindowRect = rectA == rectB && rectA == rectAAfter && rectB == rectBAfter;
        var identityMatched = RecordScenarioEndIdentity(main, log, "ime-candidate");

        var details = new Dictionary<string, object?>
        {
            ["keys"] = options.Keys,
            ["composing_at_anchor_a"] = composing,
            ["candidate_count_at_anchor_a"] = candidates,
            ["anchor_a_client"] = $"{pointA.X},{pointA.Y}",
            ["anchor_b_client"] = $"{pointB.X},{pointB.Y}",
            ["window_rect_at_anchor_a"] = rectA,
            ["window_rect_at_anchor_b"] = rectB,
            ["window_rect_unchanged_between_anchors"] = sameWindowRect,
            ["candidate_requests"] = anchorRequests,
            ["candidate_set_ok"] = anchorSetOk,
            ["candidate_get_ok"] = anchorGetOk,
            ["screenshots_are_own_window_rect_only"] = true,
            ["system_candidate_visible_in_screenshots"] = "not machine-detected",
            ["system_candidate_actually_moved"] = "not machine-detected",
            ["identity_snapshots"] = IdentityProbe.SnapshotCount,
            ["actual_active_matches_configured_profile_at_end"] = identityMatched,
            ["assertion"] = "two anchor publishes inside the own window rect, each backed by an own-window screenshot "
                + "taken with an identical window rect; the actual on-screen candidate position is NOT asserted here",
        };

        var verdict = CandidateAnchorOutcome.Evaluate(
            anchorRequests, anchorSetOk, anchorGetOk, sameWindowRect, composing);
        var notes = verdict.Status == "OBSERVED"
            ? verdict.Reason + $" (inspect the two PNGs; candidate_count_at_anchor_a={candidates})"
            : verdict.Reason;
        return new ScenarioResult("ime-candidate", verdict.Status, notes, details);
    }

    private static ScenarioResult RunLatin(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "latin", out var reason))
        {
            return new ScenarioResult("latin", "SKIP", reason, []);
        }

        var switched = TrySwitchWindowLanguage(main, log, 0x04090409, "en-US");
        if (!switched)
        {
            return new ScenarioResult("latin", "SKIP",
                "could not switch this window's input language to en-US (0x04090409); session default was not modified", []);
        }

        Pump(options.SettleMs);
        var before = new Observation(main);
        if (!script.SendAsciiText("abc", "latin"))
        {
            TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
            return Aborted("latin", script);
        }

        Pump(options.SettleMs);
        // 自动重复路径：注入连续 keydown（注入按键不会被系统自动重复，见 InputScript.SendRepeatBurst）。
        if (!script.SendRepeatBurst(0x41, "latin:repeat-a", 3, 120))
        {
            TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
            return Aborted("latin", script);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("latin-typed", 150);
        var after = new Observation(main);
        var restored = TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");

        var newTextInputs = after.TextInputs.Skip(before.TextInputs.Count).ToList();
        var details = new Dictionary<string, object?>
        {
            ["keys"] = "abc then 3 injected repeated keydowns for a",
            ["char_messages"] = after.CharMessageCount - before.CharMessageCount,
            ["unichar_messages"] = after.UnicharMessageCount - before.UnicharMessageCount,
            ["ime_char_messages"] = after.ImeCharMessageCount - before.ImeCharMessageCount,
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["text_inputs_new"] = newTextInputs,
            ["key_repeats"] = after.KeyRepeatCount - before.KeyRepeatCount,
            ["input_language_switched_for_this_window_only"] = true,
            ["input_language_restored"] = restored,
            ["real_key_repeat_note"] = "SendInput 注入的连续 keydown 被系统合并为一次 WM_KEYDOWN，未能产生自动重复；重复路径由 repeat 场景的合成边界测试覆盖",
            ["assertion"] = "at least three plain ASCII keys produced GLFW TextInput events and the window-local input language was restored",
        };
        var status = newTextInputs.Count >= 3 && restored ? "PASS" : "UNVERIFIED";
        return new ScenarioResult("latin", status,
            "plain ASCII path with real injected keys under the en-US layout (window-local switch, restored afterwards)", details);
    }

    private static ScenarioResult RunNonBmp(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "nonbmp", out var reason))
        {
            return new ScenarioResult("nonbmp", "SKIP", reason, []);
        }

        if (!TrySwitchWindowLanguage(main, log, 0x04090409, "en-US"))
        {
            return new ScenarioResult("nonbmp", "SKIP", "could not switch window input language for the synthetic boundary test", []);
        }

        Pump(options.SettleMs);
        var before = new Observation(main);
        const string emoji = "\U0001F600"; // U+1F600, UTF-16 D83D DE00
        foreach (var unit in emoji)
        {
            if (!script.SendUnicodeCodeUnit(unit, $"nonbmp:U+{(int)unit:X4}"))
            {
                TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
                return Aborted("nonbmp", script);
            }

            Pump(120);
        }

        Pump(options.SettleMs);
        main.CaptureScreenshot("nonbmp-typed", 150);
        var after = new Observation(main);
        TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");

        var newInputs = after.TextInputs.Skip(before.TextInputs.Count).ToList();
        var details = new Dictionary<string, object?>
        {
            ["injected_code_units"] = "U+D83D then U+DE00 via KEYEVENTF_UNICODE",
            ["synthetic"] = true,
            ["note"] = "synthetic boundary test: not a real IME path, and no WM_IME message was fabricated",
            ["char_messages"] = after.CharMessageCount - before.CharMessageCount,
            ["unichar_messages"] = after.UnicharMessageCount - before.UnicharMessageCount,
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["text_inputs_new"] = newInputs,
            ["text_input_utf16"] = newInputs.Select(ImeFormat.EscapeUtf16).ToList(),
            ["text_input_code_units"] = newInputs.Select(ImeFormat.CodeUnitCount).ToList(),
            ["assertion"] = "exactly one TextInput with a 2-code-unit surrogate pair from the two injected UTF-16 code units",
        };
        var status = newInputs.Count == 1 && newInputs[0] == emoji && ImeFormat.CodeUnitCount(newInputs[0]) == 2 ? "PASS" : "FAIL";
        return new ScenarioResult("nonbmp", status,
            "non-BMP through a synthetic keyboard path (surrogate pair -> single GLFW codepoint) to pin down the GLFW char contract", details);
    }

    private static ScenarioResult RunFaultInjection(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "fault", out var reason))
        {
            return new ScenarioResult("fault", "SKIP", reason, []);
        }

        var before = new Observation(main);
        // 合成边界测试 1：托管回调内主动抛异常（消息路径）。
        main.EnableFaultInjection("message", NativeMethods.WM_CHAR);
        if (!TrySwitchWindowLanguage(main, log, 0x04090409, "en-US"))
        {
            return new ScenarioResult("fault", "SKIP", "could not switch window input language", []);
        }

        Pump(400);
        if (!script.PressVirtualKey(0x41, "fault:a"))
        {
            TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
            return Aborted("fault", script);
        }

        Pump(options.SettleMs);
        // 合成边界测试 2：日志写入失败（回调边界不得依赖日志成功）。
        var failuresBefore = log.FailedWriteCount;
        main.EnableFaultInjection("log", NativeMethods.WM_CHAR);
        script.PressVirtualKey(0x42, "fault:b");
        Pump(options.SettleMs);
        TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
        var after = new Observation(main);

        var caughtMessagePath = after.CallbackExceptions - before.CallbackExceptions >= 2;
        var logWriteFailed = log.FailedWriteCount > failuresBefore;
        var chained = after.TextInputCount - before.TextInputCount >= 2;
        var details = new Dictionary<string, object?>
        {
            ["synthetic"] = true,
            ["cases"] = "message-throw (WM_CHAR), log-write-failure (WM_CHAR),",
            ["assertion"] = "both injected failures were swallowed at the native callback boundary and the chained proc still ran",
            ["callback_exceptions"] = after.CallbackExceptions - before.CallbackExceptions,
            ["log_write_failures"] = log.FailedWriteCount - failuresBefore,
            ["chain_continued_after_exception"] = chained,
            ["window_alive"] = main.Window.Exists,
        };
        var status = caughtMessagePath && logWriteFailed && main.Window.Exists ? "PASS" : "UNVERIFIED";
        return new ScenarioResult("fault", status,
            "synthetic boundary tests: a managed exception thrown in the native callback and a failing log write were both swallowed; the chained window proc still ran", details);
    }

    /// <summary>合成边界测试：恢复窗口过程失败时的判据与委托保活（需要销毁窗口，单独进程运行）。</summary>
    private static ScenarioResult RunFaultRestore(ProbeOptions options, ProbeLog log)
    {
        var main = RequireMain();
        var beforeExceptions = main.CallbackExceptionCount;
        main.EnableFaultInjection("restore", 0);
        main.Dispose();

        var details = new Dictionary<string, object?>
        {
            ["synthetic"] = true,
            ["restore_outcomes"] = main.RestoreOutcomes,
            ["subclass_restored"] = main.SubclassRestored,
            ["callback_exceptions"] = main.CallbackExceptionCount - beforeExceptions,
            ["process_survived"] = true,
            ["assertion"] = "WM_NCDESTROY restore reports failure (never uninstall-ok), the delegate is pinned, and the probe still exits cleanly",
        };
        _main = null;
        var status = !main.SubclassRestored
            && main.RestoreOutcomes.Contains("WM_NCDESTROY:failed")
            && !main.RestoreOutcomes.Contains(":ok")
            ? "PASS"
            : "FAIL";
        return new ScenarioResult("fault-restore", status,
            "synthetic boundary test: an ineffective restore is reported as failure, the delegate stays pinned, and no false success is logged", details);
    }

    private static ScenarioResult RunImeClose(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "ime-close", out var reason))
        {
            return new ScenarioResult("ime-close", "SKIP", reason, []);
        }

        var before = new Observation(main);

        if (!script.SendAsciiText(options.Keys, "ime-close"))
        {
            return Aborted("ime-close", script);
        }

        Pump(options.SettleMs);
        var composing = main.IsComposing;
        var beforeExceptions = main.CallbackExceptionCount;
        main.CaptureScreenshot("ime-close-while-composing", 200);

        // 关闭并销毁窗口：销毁过程在 Dispose 内同步派发 WM_*，子类化过程仍然装着，因此能记录
        // WM_IME_SETCONTEXT(FALSE)/WM_KILLFOCUS/WM_DESTROY/WM_NCDESTROY 以及 WM_NCDESTROY 处的恢复动作。
        main.Window.Close();
        var closeRequested = _closeRequested;
        main.Dispose();

        var restored = main.SubclassRestored;
        var stillInstalled = main.SubclassInstalled;
        var details = new Dictionary<string, object?>
        {
            ["was_composing"] = composing,
            ["composition_updates"] = main.CompositionUpdateCount - before.CompositionUpdateCount,
            ["compositions_started"] = main.CompositionId - before.CompositionId,
            ["cancellations"] = main.CancelCount - before.Cancellations,
            ["callback_exceptions"] = main.CallbackExceptionCount - beforeExceptions,
            ["close_requested"] = closeRequested,
            ["subclass_restored"] = restored,
            ["subclass_still_installed"] = stillInstalled,
            ["restore_trigger"] = "WM_NCDESTROY (see events.jsonl)",
        };
        _main = null;
        // 只有在“确实处于组合中”关闭才满足“组合中关闭”的验收语义；否则只算关闭/恢复边界 OBSERVED。
        string status;
        string notes;
        if (!restored || stillInstalled)
        {
            status = "FAIL";
            notes = "window destroyed but the original window proc was not verified restored, or the subclass is still installed";
        }
        else if (composing)
        {
            status = "PASS";
            notes = "window was closed while composing; the original window proc was restored at WM_NCDESTROY and no managed exception escaped";
        }
        else
        {
            status = "OBSERVED";
            notes = "window was closed while NOT composing (no IME composition started); this only exercises the close/restore boundary and does not verify closing during a composition";
        }

        return new ScenarioResult("ime-close", status, notes, details);
    }

    private static ScenarioResult RunManual(ProbeOptions options, ProbeLog log)
    {
        var main = RequireMain();
        var deadline = Environment.TickCount64 + (long)(options.ManualSeconds * 1000);
        var before = new Observation(main);
        var captures = 0;
        _main!.Window.Title = "TI-01 manual: type nihao, Space to commit, Esc to cancel; probe stops after "
            + options.ManualSeconds + "s";
        log.Record("probe", "manual", "manual-start", new Dictionary<string, object?>
        {
            ["seconds"] = options.ManualSeconds,
            ["instructions"] = "type the fixed test text (nihao) on the dedicated window; do not type personal content",
        });

        while (Environment.TickCount64 < deadline && !_closeRequested && main.Window.Exists && !_timedOut)
        {
            Pump(2000);
            main.CaptureScreenshot($"manual-{++captures:D2}", 0);
            if (captures >= 20)
            {
                break;
            }
        }

        var after = new Observation(main);
        var details = new Dictionary<string, object?>
        {
            ["composition_updates"] = after.CompositionUpdateCount - before.CompositionUpdateCount,
            ["result_segments"] = after.ResultSegments.Skip(before.ResultSegments.Count).ToList(),
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["captures"] = captures,
        };
        var interacted = after.CompositionUpdateCount > before.CompositionUpdateCount || after.TextInputCount > before.TextInputCount;
        return new ScenarioResult("manual", interacted ? "PASS" : "UNVERIFIED",
            interacted
                ? "human typing observed on the dedicated window"
                : "no human input arrived within the manual window; automated real-key-injection scenarios are the remaining evidence",
            details);
    }

    private static ScenarioResult RunRepeatPath(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        if (!Prepare(options, main, script, log, "repeat", out var reason))
        {
            return new ScenarioResult("repeat", "SKIP", reason, []);
        }

        if (!TrySwitchWindowLanguage(main, log, 0x04090409, "en-US"))
        {
            return new ScenarioResult("repeat", "SKIP", "could not switch window input language", []);
        }

        Pump(options.SettleMs);
        var before = new Observation(main);
        if (!script.PressVirtualKey(0x41, "repeat:initial-down", 80))
        {
            TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
            return Aborted("repeat", script);
        }

        Pump(200);
        // 真实注入的三次连续 keydown 会被系统合并成一次 WM_KEYDOWN（见 probe-all-2 证据），
        // 因此这里用带“前一状态按下”位的 post 消息做标注为合成的重复路径边界测试。
        script.PostSyntheticRepeatKeyDown(0x41, "repeat:repeat-1");
        Pump(150);
        script.PostSyntheticRepeatKeyDown(0x41, "repeat:repeat-2");
        Pump(150);
        if (!script.PressVirtualKey(0x41, "repeat:release-only"))
        {
            TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
            return Aborted("repeat", script);
        }

        Pump(options.SettleMs);
        TrySwitchWindowLanguage(main, log, 0x08040804, "zh-CN");
        var after = new Observation(main);
        var details = new Dictionary<string, object?>
        {
            ["synthetic_repeat_portion"] = true,
            ["real_injected_repeat_coalesced"] = true,
            ["key_repeat_events"] = after.KeyRepeatCount - before.KeyRepeatCount,
            ["key_down_events"] = after.KeyDownCount - before.KeyDownCount,
            ["char_messages"] = after.CharMessageCount - before.CharMessageCount,
            ["text_input_events"] = after.TextInputCount - before.TextInputCount,
            ["limitation"] = "the repeat notification itself is posted (labelled synthetic) because SendInput cannot "
                + "produce OS auto-repeat; WM_CHAR duplication is still exercised through TranslateMessage, but the "
                + "IME's consumption of repeated keys (VK_PROCESSKEY) needs a real physical key check",
        };
        var repeatEvents = after.KeyRepeatCount - before.KeyRepeatCount;
        // 这里的重复通知来自合成的 posted WM_KEYDOWN，不是系统真实自动重复；因此只算 OBSERVED，不判 PASS。
        var status = repeatEvents >= 2 ? "OBSERVED" : "UNVERIFIED";
        var notes = repeatEvents >= 2
            ? "synthetic boundary: a posted WM_KEYDOWN with the previous-key-down bit produced GLFW_REPEAT; this is NOT real OS auto-repeat, and real auto-repeat with the IME active remains UNVERIFIED"
            : "no repeat notification was observed; real auto-repeat remains UNVERIFIED";
        return new ScenarioResult("repeat", status, notes, details);
    }

    private static bool RunWarmup(ProbeOptions options, ProbeLog log, InputScript script)
    {
        var main = RequireMain();
        switch (options.Warmup)
        {
            case "none":
                return true;
            case "shift":
                log.Record("script", "sendinput", "warmup", new Dictionary<string, object?>
                {
                    ["keys"] = "VK_SHIFT (mode toggle as a user would press)",
                });
                if (!script.PressVirtualKey(0x10, "warmup:shift", 60))
                {
                    return false;
                }

                break;
            case "ctrl-space":
                // 明确拒绝，而不是保留一个无法闭合的路径：组合键在“第二次 keydown 之后失焦/中止”时，
                // SendRaw 依保护规则会拒绝发送 keyup（绝不向非前台窗口注入），因此“失败也恢复键态”无法保证。
                // 该预热只是可选诊断，不为它新建可注入键盘后端。
                log.Record("script", "sendinput", "warmup-rejected", new Dictionary<string, object?>
                {
                    ["warmup"] = "ctrl-space",
                    ["reason"] = "a modifier chord cannot guarantee its key-up if the foreground is lost mid-chord; "
                        + "the probe will not claim key-state recovery it cannot enforce",
                    ["available_warmups"] = "none | shift",
                });
                throw new ArgumentException(
                    "--warmup ctrl-space is intentionally rejected: a chord's key-up cannot be guaranteed when the "
                    + "foreground is lost mid-chord. Use --warmup none or shift.");
            default:
                throw new ArgumentException($"unknown warmup {options.Warmup}");
        }

        Pump(options.SettleMs);
        main.LogCurrentImeState($"after-warmup-{options.Warmup}");
        return true;
    }

    private static bool Prepare(ProbeOptions options, ImeProbeWindow main, InputScript script, ProbeLog log, string scenario, out string reason)
    {
        if (_timedOut)
        {
            reason = "wall-clock guard expired";
            return false;
        }

        if (_closeRequested || !main.Window.Exists)
        {
            reason = "window already closed";
            return false;
        }

        if (!main.Window.IsFocused)
        {
            // 后台环境里“探针进程启动”常常不会自动把窗口带到前台。这里给用户一个明确的窗口来点击：
            // 只读取前台状态并等待，不反复争抢、不调用 SetForegroundWindow 循环。
            // 前台一旦到手就继续；直到超时仍不是本窗口就跳过本场景（不把按键发给任何其他窗口）。
            var waitDeadline = Environment.TickCount64 + options.ForegroundWaitMs;
            var polls = 0;
            var activationAttempts = 0;
            while (Environment.TickCount64 < waitDeadline)
            {
                if (_closeRequested || _timedOut || !main.Window.Exists)
                {
                    break;
                }

                var foregroundNow = NativeMethods.GetForegroundWindow();
                if (foregroundNow == main.Handle)
                {
                    break;
                }

                // 每约 2 秒只尝试一次把窗口带到前台，并立刻放弃：这不是争抢，只是给用户一个可见、可点击的
                // 窗口。失败绝不循环轰炸（Windows 前台锁定会静默拒绝），最终仍由用户点击决定。
                if (polls % 8 == 0)
                {
                    activationAttempts++;
                    NativeMethods.SetForegroundWindow(main.Handle);
                }

                polls++;
                Pump(250);
            }

            log.Record("script", "sendinput", "foreground-wait", new Dictionary<string, object?>
            {
                ["scenario"] = scenario,
                ["wait_ms"] = options.ForegroundWaitMs,
                ["polls"] = polls,
                ["bounded_activation_attempts"] = activationAttempts,
                ["foreground"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}",
                ["is_own_window"] = NativeMethods.GetForegroundWindow() == main.Handle,
                ["note"] = "the probe makes at most one bounded activation attempt every ~2s and never loops on it; "
                    + "click the probe window to run the key-injection scenarios",
            });

            Pump(200);
        }

        if (NativeMethods.GetForegroundWindow() != main.Handle)
        {
            reason = "probe window is not the foreground window; refusing to send key events";
            log.Record("script", "sendinput", "scenario-skipped", new Dictionary<string, object?>
            {
                ["scenario"] = scenario,
                ["reason"] = reason,
                ["foreground"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}",
            });
            return false;
        }

        // 场景起点：本窗口既是前台、也有活动上下文，此刻只读查一次实际输入法身份。
        TsfProfile.ReadActiveProfile(log, $"{scenario}:begin");
        IdentityProbe.Record(log, main, $"{scenario}:begin");
        main.LogCurrentInputLanguage($"{scenario}:begin");
        main.LogCurrentImeState($"{scenario}:begin");
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 场景结束时的现场快照：先泵出所有延迟日志/身份查询，再只读核对实际活动 profile 是否仍是本场景配置的
    /// 目标输入法。返回结论而不是抛异常；查询失败与身份不匹配都如实返回 false。
    /// </summary>
    private static bool RecordScenarioEndIdentity(ImeProbeWindow main, ProbeLog log, string scenario)
    {
        // 组合回调里登记的延迟查询必须在这里落地，保证日志里“首次组合/结果时刻”确实有对应的查询记录。
        Pump(120);
        var matched = IdentityProbe.Record(log, main, $"{scenario}:end");
        main.LogCurrentImeState($"{scenario}:end");
        log.Record("probe", "identity", "scenario-end-identity", new Dictionary<string, object?>
        {
            ["scenario"] = scenario,
            ["actual_active_matches_configured_profile"] = matched,
            ["snapshots_recorded"] = IdentityProbe.SnapshotCount,
            ["note"] = "read-only GetActiveProfile; a mismatch is reported as a fact, it is not folded into a PASS",
        });
        return matched;
    }

    private static ScenarioResult Aborted(string name, InputScript script)
        => new(name, script.Aborted ? "SKIP" : "FAIL",
            script.Aborted ? "input injection aborted (foreground lost); nothing was sent outside the probe window" : "input injection failed", []);

    private static bool TrySwitchWindowLanguage(ImeProbeWindow main, ProbeLog log, uint hklValue, string tag)
    {
        var hkl = new IntPtr(unchecked((int)hklValue));
        NativeMethods.PostMessage(main.Handle, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        var threadId = NativeMethods.GetWindowThreadProcessId(main.Handle, out _);
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            Pump(100);
            var current = NativeMethods.GetKeyboardLayout(threadId);
            if (current == hkl)
            {
                log.Record("script", "user32-message", "window-language-switch", new Dictionary<string, object?>
                {
                    ["target_hkl"] = $"0x{hklValue:X8}",
                    ["tag"] = tag,
                    ["current_hkl"] = $"0x{current.ToInt64():X8}",
                    ["scope"] = "this window's thread only (WM_INPUTLANGCHANGEREQUEST); session default not modified",
                });
                return true;
            }
        }

        log.Record("script", "user32-message", "window-language-switch-failed", new Dictionary<string, object?>
        {
            ["target_hkl"] = $"0x{hklValue:X8}",
            ["tag"] = tag,
            ["current_hkl"] = $"0x{NativeMethods.GetKeyboardLayout(threadId).ToInt64():X8}",
        });
        return false;
    }

    /// <summary>泵事件并渲染指定时长；墙钟守卫到期后置 <c>_timedOut</c>。</summary>
    private static void Pump(int milliseconds)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, milliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (!PumpOnce())
            {
                return;
            }
        }
    }

    private static bool PumpOnce()
    {
        if (Environment.TickCount64 > _deadlineTicks)
        {
            _timedOut = true;
            return false;
        }

        if (_closeRequested)
        {
            return false;
        }

        try
        {
            GLFW.PollEvents();
        }
        catch (Exception ex)
        {
            // 只有“已请求销毁/窗口已消失”时的异常可以忽略；其他异常必须记录，不能吞掉探针失败。
            var expected = _closeRequested || (_main is null || !_main.Window.Exists);
            _main?.LogPumpException("PollEvents", ex, expected);
        }

        foreach (var window in new[] { _main, _focusTarget })
        {
            if (window is null || !window.Window.Exists)
            {
                continue;
            }

            try
            {
                window.RenderFrame();
            }
            catch (Exception ex)
            {
                window.LogPumpException("RenderFrame", ex, expected: false);
            }
        }

        // 截图只在消息返回且画面更新之后执行；原生回调里只登记请求，绝不在 WndProc 内休眠等待 UI。
        _main?.FlushDeferredCaptures();
        _focusTarget?.FlushDeferredCaptures();

        // 同理：原生回调（WM_IME_STARTCOMPOSITION/COMPOSITION）里只登记身份查询标签，
        // 真正的只读 GetActiveProfile 在这里、窗口过程已退出后执行 —— 查询时刻紧贴现场且无递归泵。
        if (_main is not null && IdentityProbe.HasPending)
        {
            IdentityProbe.FlushPending(log: _main.Log, window: _main);
        }

        Thread.Sleep(2);
        return true;
    }

    private static ImeProbeWindow RequireMain()
        => _main ?? throw new InvalidOperationException("main window is not available");

    /// <summary>恢复受控临时 profile 并用 GetProfile/GetActiveProfile 校验；幂等，可在异常路径重复调用。</summary>
    private static void RestoreTemporaryProfile()
    {
        if (_temporaryProfile is null || _profileRestoreRecorded)
        {
            return;
        }

        _profileRestoreRecorded = true;
        try
        {
            _temporaryProfile.Dispose();
        }
        catch (Exception ex)
        {
            Results.Add(new ScenarioResult("profile-temporary-restore", "FAIL",
                $"temporary profile restore threw {ex.GetType().Name}: {ex.Message}", []));
            return;
        }

        Results.Add(new ScenarioResult(
            "profile-temporary-restore",
            _temporaryProfile.Restored ? "PASS" : "FAIL",
            _temporaryProfile.Restored
                ? "temporary Microsoft Pinyin activation was disabled and the original active profile verified restored (GetProfile/GetActiveProfile)"
                : $"profile restore verification failed: {_temporaryProfile.Failure}",
            new Dictionary<string, object?>
            {
                ["restored"] = _temporaryProfile.Restored,
                ["failure"] = _temporaryProfile.Failure,
            }));
    }

    private static void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        // 兜底：正常路径已在 UninitializeCom 之前恢复临时 profile；这里覆盖异常提前退出的情况。
        try
        {
            RestoreTemporaryProfile();
        }
        catch (Exception)
        {
            // 恢复失败已由 RestoreTemporaryProfile 记录；退出不应再抛。
        }

        try
        {
            _main?.RestoreImeState();
            _focusTarget?.UninstallSubclass();
            _main?.UninstallSubclass();
        }
        catch (Exception)
        {
            // 恢复过程本身不应影响退出。
        }
    }

    private static Dictionary<string, object?> BuildEnvironmentReport(ImeProbeWindow main)
    {
        var report = new Dictionary<string, object?>
        {
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["os_architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["process_architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["process_64bit"] = Environment.Is64BitProcess,
            ["dotnet"] = Environment.Version.ToString(),
            ["session_id"] = Process.GetCurrentProcess().SessionId,
            ["user_interactive"] = Environment.UserInteractive,
            ["glfw_version_string"] = NativeMethods.GetGlfwVersionString(),
            ["hwnd"] = $"0x{main.Handle.ToInt64():X}",
            ["window_class"] = NativeMethods.GetWindowClassName(main.Handle),
            ["window_dpi"] = main.WindowDpi,
            ["client_size"] = main.DescribeClientSize(),
            ["caret_client"] = $"{main.CaretClientPoint.X},{main.CaretClientPoint.Y}",
            ["screen"] = $"{NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)}x{NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)}",
            ["virtual_screen"] = $"{NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN)},{NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN)} "
                + $"{NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN)}x{NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)}",
        };

        report["loaded_modules"] = DescribeLoadedModules();
        report["module_hashes"] = DescribeModuleHashes();
        report["apartment"] = new Dictionary<string, object?>
        {
            ["entry_attribute"] = "[STAThread] on Main",
            ["managed_thread_id"] = Environment.CurrentManagedThreadId,
            ["win32_thread_id"] = NativeMethods.CurrentWin32ThreadId(),
            ["thread_apartment_state"] = Thread.CurrentThread.GetApartmentState().ToString(),
        };
        report["source_matching_boundary"] = "native GLFW binary is OpenTK.redist.glfw 3.4.0.44 (glfw3.dll product version 3.4.0); "
            + "behaviour was cross-checked against the upstream glfw/glfw tag 3.4 sources, which is not proof that the redist build is byte-identical to that tag";
        report["opentk_assembly"] = DescribeAssembly(typeof(NativeWindow).Assembly);
        report["opentk_get_win32_window_binding"] = typeof(GLFW).GetMethod(
            "GetWin32Window", BindingFlags.Public | BindingFlags.Static) is not null;
        report["system_dlls"] = DescribeSystemDlls();
        return report;
    }

    private static List<Dictionary<string, object?>> DescribeLoadedModules()
    {
        var modules = new List<Dictionary<string, object?>>();
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var name = module.ModuleName;
                if (name.Contains("glfw", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("opentk", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("imm32", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("msctf", StringComparison.OrdinalIgnoreCase))
                {
                    modules.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["path"] = module.FileName,
                        ["file_version"] = module.FileVersionInfo.FileVersion,
                        ["product_version"] = module.FileVersionInfo.ProductVersion,
                        ["size_bytes"] = module.ModuleMemorySize,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            modules.Add(new Dictionary<string, object?> { ["error"] = ex.Message });
        }

        return modules;
    }

    /// <summary>记录实际加载的原生库/托管程序集 SHA256，供独立核对二进制身份。</summary>
    private static List<Dictionary<string, object?>> DescribeModuleHashes()
    {
        var results = new List<Dictionary<string, object?>>();
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var name = module.ModuleName;
                if (!name.Contains("glfw", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("OpenTK", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("imm32", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = File.OpenRead(module.FileName);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                results.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["path"] = module.FileName,
                    ["sha256"] = hash,
                    ["size_bytes"] = new FileInfo(module.FileName).Length,
                });
            }
        }
        catch (Exception ex)
        {
            results.Add(new Dictionary<string, object?> { ["error"] = ex.Message });
        }

        return results;
    }

    private static string ApartmentTypeName(int apartmentType) => apartmentType switch
    {
        -1 => "APTTYPE_CURRENT",
        0 => "APTTYPE_STA",
        1 => "APTTYPE_MTA",
        2 => "APTTYPE_NA",
        3 => "APTTYPE_MAINSTA",
        _ => $"APTTYPE({apartmentType})",
    };

    private static Dictionary<string, object?> DescribeAssembly(Assembly assembly)    {
        var location = assembly.Location;
        var version = FileVersionInfo.GetVersionInfo(location);
        return new Dictionary<string, object?>
        {
            ["full_name"] = assembly.FullName,
            ["location"] = location,
            ["file_version"] = version.FileVersion,
            ["product_version"] = version.ProductVersion,
        };
    }

    private static List<Dictionary<string, object?>> DescribeSystemDlls()
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var name in new[] { "imm32.dll", "msctf.dll", "user32.dll", "glfw3.dll" })
        {
            try
            {
                var path = Path.Combine(Environment.SystemDirectory, name);
                if (File.Exists(path))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    result.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["path"] = path,
                        ["file_version"] = info.FileVersion,
                        ["size_bytes"] = new FileInfo(path).Length,
                    });
                }
            }
            catch (Exception ex)
            {
                result.Add(new Dictionary<string, object?> { ["name"] = name, ["error"] = ex.Message });
            }
        }

        return result;
    }

    private static void WriteSummary(ProbeLog log, string summaryPath, string summaryTextPath)
    {
        var summary = new Dictionary<string, object?>
        {
            ["scenario"] = Results.Select(r => r.Name).ToList(),
            ["results"] = Results.Select(r => new Dictionary<string, object?>
            {
                ["name"] = r.Name,
                ["status"] = r.Status,
                ["notes"] = r.Notes,
                ["details"] = r.Details,
            }).ToList(),
            ["status_legend"] = StatusDescriptions,
            ["timed_out"] = _timedOut,
            ["events"] = log.LastSequence,
            ["log"] = log.LogPath,
            ["identity_snapshots"] = IdentityProbe.SnapshotCount,
            ["identity_snapshot_note"] = "read-only GetActiveProfile snapshots recorded at scenario begin/end and at the first real "
                + "composition/result moments; see kind=identity-snapshot in events.jsonl",
        };

        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        var text = new StringBuilder();
        text.AppendLine($"TI-01 IME probe summary ({DateTimeOffset.Now:O})");
        text.AppendLine($"events file: {log.LogPath}");
        text.AppendLine($"events: {log.LastSequence}; timed_out: {_timedOut}");
        foreach (var result in Results)
        {
            text.AppendLine();
            text.AppendLine($"[{result.Status}] {result.Name}: {result.Notes}");
            if (result.Details.Count > 0)
            {
                text.AppendLine("  " + JsonSerializer.Serialize(result.Details));
            }
        }

        File.WriteAllText(summaryTextPath, text.ToString());
        Console.WriteLine(text.ToString());
    }

    private sealed class Observation
    {
        internal Observation(ImeProbeWindow window)
        {
            CompositionUpdateCount = window.CompositionUpdateCount;
            CompositionId = window.CompositionId;
            ResultSegments = window.ResultSegments.ToList();
            TextInputs = window.TextInputs.ToList();
            TextInputCount = window.TextInputCount;
            ImeCharMessageCount = window.ImeCharMessageCount;
            CharMessageCount = window.CharMessageCount;
            UnicharMessageCount = window.UnicharMessageCount;
            KeyRepeatCount = window.KeyRepeatCount;
            KeyDownCount = window.KeyDownCount;
            CandidateRequestCount = window.CandidateRequestCount;
            CallbackExceptions = window.CallbackExceptionCount;
            Cancellations = window.CancelCount;
            AnchorSetOkCount = window.AnchorSetOkCount;
            AnchorGetOkCount = window.AnchorGetOkCount;
            ResultFlagCommitCount = window.ResultFlagCommitCount;
            SnapshotOnlyResultCount = window.SnapshotOnlyResultCount;
            IsComposing = window.IsComposing;
            CurrentImeDescription = window.CurrentImeDescription;
            CurrentImeFileName = window.CurrentImeFileName;
            ResultAndPreeditSameMessage = window.ResultAndPreeditSameMessage;
            CommitSnapshots = window.CommitSnapshots.ToList();
        }

        /// <summary>
        /// 观察时刻已冻结的逐结果快照（结果串 + 所属 compositionId + 该结果的预编辑历史）。
        /// 每个结果自带其历史，避免用“最后一段会话”的历史去判定早先的结果。
        /// </summary>
        internal List<CommitSnapshot> CommitSnapshots { get; }

        internal int CompositionUpdateCount { get; }

        internal int CompositionId { get; }

        internal List<string> ResultSegments { get; }

        internal List<string> TextInputs { get; }

        internal int TextInputCount { get; }

        internal int ImeCharMessageCount { get; }

        internal int CharMessageCount { get; }

        internal int UnicharMessageCount { get; }

        internal int KeyRepeatCount { get; }

        internal int KeyDownCount { get; }

        internal int CandidateRequestCount { get; }

        internal int CallbackExceptions { get; }

        internal int Cancellations { get; }

        internal int AnchorSetOkCount { get; }

        internal int AnchorGetOkCount { get; }

        internal int ResultFlagCommitCount { get; }

        internal int SnapshotOnlyResultCount { get; }

        internal bool IsComposing { get; }

        internal string CurrentImeDescription { get; }

        internal string CurrentImeFileName { get; }

        internal bool ResultAndPreeditSameMessage { get; }
    }
}
