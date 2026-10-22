using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

internal static class Program
{
    /// <summary>
    /// 进程入口处（Main 第一行平台守卫之后、任何日志/窗口/COM 对象之前）的真实 COM 套间（R17 诊断）。
    /// CLR 会按入口属性初始化主线程套间：没有 <c>[STAThread]</c> 时默认 MTA，
    /// 因此这里若已是 MTA，则“条件性 STA”不可能在主线程上实现，宿主必须自选线程。
    /// 记录顺序：先平台判断（非 Windows 直接返回 2，不加载 ole32），再本诊断。
    /// </summary>
    internal static string ApartmentAtMainEntry { get; private set; } = "(not probed)";

    /// <summary>进程入口线程的托管线程 ID：用于证明 STA 宿主确实运行在自选线程上。</summary>
    internal static int EntryManagedThreadId { get; private set; }

    private static int Main(string[] args)
    {
        // R19：平台守卫必须在任何 ole32/COM 调用之前——非 Windows 上不得先尝试加载 ole32。
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("CSharp.UI.TsfHostCheck only runs on Windows (TSF/GDI).");
            return 2;
        }

        // 平台确认后、任何日志/窗口/COM 对象创建前记录入口套间（Windows 专用诊断）。
        EntryManagedThreadId = Environment.CurrentManagedThreadId;
        var entryHr = NativeMethods.CoGetApartmentType(out var entryType, out var entryQualifier);
        ApartmentAtMainEntry = entryHr >= 0
            ? HostApartment.DescribeApartment(entryType, entryQualifier)
            : TsfHResult.Describe(entryHr);

        if (!TryParse(args, out var parsed, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 1;
        }

        if (parsed.Help)
        {
            PrintUsage();
            return 0;
        }

        if (parsed.SelfTest || parsed.NativeSelfTest)
        {
            var exitCode = 0;
            if (parsed.SelfTest)
            {
                exitCode = Math.Max(exitCode, SelfTest.Run());
            }

            if (parsed.NativeSelfTest)
            {
                exitCode = Math.Max(exitCode,
                    NativeSmoke.Run(parsed.ApartmentFilter, parsed.Options!.OutDir,
                        parsed.Options.UiElementEnabledOnly));
            }

            return exitCode;
        }

        if (parsed.Scenario is { } scenario && !ScenarioRunner.IsKnownScenario(scenario))
        {
            Console.Error.WriteLine($"Unknown scenario '{scenario}'.");
            PrintUsage();
            return 1;
        }

        return RunHost(parsed.Options!);
    }

    /// <summary>
    /// 宿主线程选择（R17）。MTA 是默认：进程入口线程已被运行时初始化为 MTA，保持既有真实路径。
    /// STA 只能在自选宿主线程上实现（入口线程在 Main 之前就被固定为 MTA，实测 type=1），
    /// 该线程在启动前设置 STA，窗口创建、TSF 接入、消息循环与清理全部留在这一条线程上。
    /// </summary>
    private static int RunHost(ScenarioOptions options)
    {
        var selection = HostApartment.SelectHostThread(options.Sta);
        if (selection == HostApartment.HostThreadSelection.ProcessEntry)
        {
            return RunHostCore(options, selection);
        }

        var exitCode = 2;
        Exception? failure = null;
        var hostThread = new Thread(() =>
        {
            try
            {
                exitCode = RunHostCore(options, selection);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = false,
            Name = "tsf-host-sta",
        };

        if (OperatingSystem.IsWindows())
        {
            hostThread.SetApartmentState(ApartmentState.STA);
        }

        hostThread.Start();
        hostThread.Join();

        if (failure is not null)
        {
            Console.Error.WriteLine(
                $"STA host thread failed: {failure.GetType().Name}: {failure.Message}");
            return 1;
        }

        return exitCode;
    }

    private static int RunHostCore(ScenarioOptions options, HostApartment.HostThreadSelection threadSelection)
    {
        Directory.CreateDirectory(options.OutDir);
        using var log = new ProbeLog(Path.Combine(options.OutDir, "tsf-host-check.jsonl"));
        var traceSink = new ProbeTsfTrace(log);
        TsfTrace trace = traceSink.Write;

        var hostManagedThread = Environment.CurrentManagedThreadId;
        var hostWin32Thread = NativeMethods.CurrentWin32ThreadId();
        trace("env", "host-thread", new Dictionary<string, object?>
        {
            ["selection"] = threadSelection.ToString(),
            ["managed_thread_id"] = hostManagedThread,
            ["entry_managed_thread_id"] = EntryManagedThreadId,
            ["runs_on_entry_thread"] = hostManagedThread == EntryManagedThreadId,
            ["win32_thread_id"] = hostWin32Thread,
            ["requested_apartment"] = HostApartment.DescribeRequested(options.Sta),
        });

        // R17：宿主线程套间必须在创建任何 HWND/COM 对象之前、在真实运行线程上确定。
        // 可见运行与隐藏回归共用这一处准备；MTA 是默认，只有显式 --apartment sta 才请求 STA。
        using var apartment = HostApartment.Prepare(options.Sta, trace,
            options.HiddenHost ? "hidden-host" : "host", ApartmentAtMainEntry);
        if (apartment.HResult < 0)
        {
            log.Record("host", "host", "apartment-unavailable", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(apartment.HResult),
                ["requested"] = HostApartment.DescribeRequested(options.Sta),
                ["thread_selection"] = threadSelection.ToString(),
                ["apartment_at_main_entry"] = ApartmentAtMainEntry,
                ["apartment_before"] = apartment.BeforeDescription,
                ["apartment"] = apartment.Description,
                ["win32_thread_id"] = apartment.Win32ThreadId,
                ["managed_thread_id"] = apartment.ManagedThreadId,
            });
            return 2;
        }

        // 布局调用链诊断（默认关闭）：只给既有事件补充归因/配对字段，不改变锁与通知时序。
        var chain = new LayoutChainTrace(options.TraceLayoutChain);
        var store = new AcpTextStore(trace, chain);
        var recorder = new BindingRecorder(store, trace);
        using var window = new TsfHostWindow(store, log, trace);
        // R29B：childInputSurface 直通真实 Create —— opt-in 时创建真实 root->viewport->input
        // 三层窗口；默认 false 完全保留旧单窗口行为。hidden-host 下 child 也绝不 show/focus。
        // R34D：hostDrawnCandidates 直通 Create —— 只有显式启用本模式才创建候选 overlay；
        // overlay 是候选唯一展示通道，创建失败如实使本模式启动失败（不降级为"无 overlay 运行"）。
        if (!window.Create(options.WindowWidth, options.WindowHeight,
                options.HiddenHost
                    ? (options.ChildInputSurface
                        ? "CSharp.UI TSF prototype (hidden child input surface regression)"
                        : "CSharp.UI TSF prototype (hidden host regression)")
                    : "CSharp.UI TSF prototype — click this window to grant focus",
                show: !options.HiddenHost,
                childInputSurface: options.ChildInputSurface,
                hostDrawnCandidates: options.HostDrawnCandidates))
        {
            log.Record("host", "host", "create-failed", null);
            return 2;
        }

        // 应用通知必须在 RequestLock 返回后的窗口安全阶段发送，绝不在调用栈内回送。
        store.NotificationDispatcher = action => window.PostAlways(action);

        // R29B：TSF 关联/焦点归属 window.InputHwnd —— opt-in 为真实 input child，默认 off 为 root
        // （InputHwnd 在非 opt-in 下恰好返回 root，行为完全不变）。root 仍是前台请求、
        // 截图捕获、dispatcher 与生命周期的句柄，绝不由 child 冒充。
        var session = new TsfSessionHost(store, window.InputHwnd, log, trace, options.Sta,
            () => window.RequestRelayout("tsf-callback"), apartment,
            uiElementEnabledOnly: options.UiElementEnabledOnly);

        var engageHr = session.Engage();
        if (engageHr < 0)
        {
            log.Record("host", "host", "engage-failed", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(engageHr),
                ["apartment"] = session.Apartment,
            });
            session.Cleanup();
            return 2;
        }

        window.AttachSession(session);

        // R34D：真实呈现器接线——只在 --host-drawn-candidates 显式启用时实例化。
        // observer 在 Engage 内已创建并 AdviseSink；此处于任何输入/焦点激活之前把
        // 同一 presenter 注入并打开候选专属抑制，UI 线程上的 Apply/Hide/Invalidate
        // 全部经真实 overlay 通道。缺 observer 或 overlay 未建成都如实启动失败。
        HostDrawnCandidatePresenter? hostDrawnPresenter = null;
        if (options.HostDrawnCandidates)
        {
            var observer = session.UiElements;
            if (observer is null || !window.OverlayAlive)
            {
                log.Record("host", "host", "host-drawn-wiring-failed", new Dictionary<string, object?>
                {
                    ["observer_available"] = observer is not null,
                    ["overlay_alive"] = window.OverlayAlive,
                });
                session.Cleanup();
                return 2;
            }

            hostDrawnPresenter = new HostDrawnCandidatePresenter(
                () => window.CreateCandidateOverlayChannel(), log, trace);
            observer.HostDrawnPresenter = hostDrawnPresenter;
            observer.SuppressCandidates = true;
            trace("host", "host-drawn-wired", new Dictionary<string, object?>
            {
                ["overlay_hwnd"] = window.OverlayAlive,
            });
        }

        if (options.HiddenHost)
        {
            var hiddenExit = RunHiddenHost(window, store, recorder, session, log, trace, apartment,
                threadSelection, options.ProbeContextView, options.TraceLayoutChain,
                options.ChildInputSurface);
            ApartmentShutdown(apartment, log, options, session, recorder, store, hiddenExit);
            return hiddenExit;
        }

        var runner = new ScenarioRunner(window, store, recorder, session, log, trace, options);
        var exitCode = 1;
        var runnerCompleted = 0;
        var watchdogFired = 0;
        var runnerThread = new Thread(() =>
        {
            try
            {
                exitCode = runner.Run();
            }
            catch (Exception ex)
            {
                trace("host", "scenario-exception", new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                });
                exitCode = 1;
            }
            finally
            {
                Volatile.Write(ref runnerCompleted, 1);
                // runner 正常完成/异常/前置不可用后主动结束自有 UI，不等看门狗或用户关闭。
                window.RequestClose();
            }
        })
        {
            IsBackground = true,
            Name = "tsf-scenario",
        };

        using var watchdog = new Timer(_ =>
        {
            Interlocked.Exchange(ref watchdogFired, 1);
            trace("host", "watchdog-timeout", new Dictionary<string, object?>
            {
                ["timeout_ms"] = options.TimeoutMs,
            });
            window.RequestClose();
        }, null, options.TimeoutMs, Timeout.Infinite);

        runnerThread.Start();
        window.RunMessageLoop();

        var completedBeforeExit = Volatile.Read(ref runnerCompleted) == 1;
        var joined = runnerThread.Join(TimeSpan.FromSeconds(15));
        if (!joined)
        {
            trace("host", "runner-join-timeout", null);
        }

        window.RequestClose();
        exitCode = ProgramOutcome.Resolve(
            exitCode,
            completedBeforeExit,
            Volatile.Read(ref watchdogFired) == 1,
            joined);

        session.Cleanup();
        // R34D：presenter 先清理（End 全部视图 + 立即清屏），再销毁 overlay/窗口——
        // 绝不把仍引用已销毁 HWND 的呈现器留到窗口之后。
        hostDrawnPresenter?.Dispose();
        window.Dispose();

        ApartmentShutdown(apartment, log, options, session, recorder, store, exitCode);
        return exitCode;
    }

    /// <summary>
    /// 套间租约的成对释放与统一 finish 记录：必须在会话/窗口清理之后调用，
    /// 保证 TSF 对象销毁期间 COM 仍处于初始化状态。
    /// </summary>
    private static void ApartmentShutdown(HostApartment apartment, ProbeLog log, ScenarioOptions options,
        TsfSessionHost session, BindingRecorder recorder, AcpTextStore store, int exitCode)
    {
        apartment.Dispose();
        log.Record("host", "host", "finish", new Dictionary<string, object?>
        {
            ["mode"] = options.HiddenHost ? "hidden-host" : "scenario",
            ["exit_code"] = exitCode,
            ["writes"] = recorder.Writes,
            ["cleanup_failures"] = session.CleanupFailures,
            ["composing"] = store.IsComposing,
            ["apartment"] = session.Apartment,
            ["apartment_matches_requested"] = apartment.ReachedRequestedApartment,
            ["owns_com_reference"] = apartment.OwnsReference,
            ["text"] = ImeFormat.EscapeUtf16(store.Document.Text),
        });
    }

    /// <summary>
    /// 真实隐藏宿主的启动路径回归（R17）：复用与可见运行**完全相同**的套间准备、
    /// 窗口创建、TSF 接入与清理路径，只去掉 ShowWindow、前台请求、按键注入与真实场景。
    /// 该路径不得显示窗口、不得请求前台、不得发送任何输入。
    /// </summary>
    private static int RunHiddenHost(TsfHostWindow window, AcpTextStore store, BindingRecorder recorder,
        TsfSessionHost session, ProbeLog log, TsfTrace trace, HostApartment apartment,
        HostApartment.HostThreadSelection threadSelection, bool probeContextView, bool traceLayoutChain,
        bool childInputSurface)
    {
        var failed = new List<string>();
        var hwnd = window.Hwnd;
        // R29B：真实输入 HWND —— opt-in 为 input child，默认 off 为 root（InputHwnd 回落 root）。
        var inputHwnd = window.InputHwnd;
        var viewportHwnd = window.ViewportHwnd;
        var ownWin32Thread = NativeMethods.CurrentWin32ThreadId();
        var ownManagedThread = Environment.CurrentManagedThreadId;
        var foregroundDuringRun = NativeMethods.GetForegroundWindow();
        var runsOnEntryThread = ownManagedThread == EntryManagedThreadId;

        void Check(string name, bool ok, string detail)
        {
            trace("hidden-host", ok ? "check-pass" : "check-fail", new Dictionary<string, object?>
            {
                ["check"] = name,
                ["pass"] = ok,
                ["detail"] = detail,
            });
            if (!ok)
            {
                failed.Add(name);
            }
        }

        Check("apartment-reached", apartment.ReachedRequestedApartment,
            $"requested={HostApartment.DescribeRequested(apartment.RequestedSta)} before={apartment.BeforeDescription} after={apartment.Description}");
        Check("host-thread-selection",
            apartment.RequestedSta ? !runsOnEntryThread : runsOnEntryThread,
            $"selection={threadSelection} requested={HostApartment.DescribeRequested(apartment.RequestedSta)} runs_on_entry_thread={runsOnEntryThread} entry={EntryManagedThreadId} host={ownManagedThread}");
        Check("session-reports-host-lease", session.Apartment == apartment.Description,
            $"session={session.Apartment} host={apartment.Description}");
        Check("single-host-thread",
            apartment.Win32ThreadId == ownWin32Thread && apartment.ManagedThreadId == ownManagedThread
            && window.UiThreadId == ownWin32Thread,
            $"host={apartment.Win32ThreadId}/{apartment.ManagedThreadId} now={ownWin32Thread}/{ownManagedThread} ui={window.UiThreadId}");
        Check("window-hidden", !NativeMethods.IsWindowVisible(hwnd), $"hwnd=0x{hwnd.ToInt64():X}");
        Check("never-show-window", window.ShowWindowCalls == 0, $"show_window_calls={window.ShowWindowCalls}");
        Check("no-foreground-request", foregroundDuringRun != hwnd,
            $"foreground_during_run=0x{foregroundDuringRun.ToInt64():X} own=0x{hwnd.ToInt64():X}");
        Check("tsf-engaged", session.Engaged, $"client_id={session.ClientId}");

        // R29B opt-in：root->viewport->input 真实 child 拓扑与 TSF 关联全部指向 input。
        // 每项检查都记录**期望与实际**（原始句柄/拥有者 pid/tid），失败如实列出；
        // 只读查询（GetParent/GetWindowThreadProcessId/store.GetWnd/GetFocus）绝不改变
        // focus/show/输入状态。非 opt-in 运行完全不做以下查询，行为与旧版逐字节一致。
        if (childInputSurface)
        {
            // 原始句柄与拥有者先取一次：后续检查与 trace 用同一组事实。
            var rootTid = NativeMethods.GetWindowThreadProcessId(hwnd, out var rootPid);
            var viewportTid = NativeMethods.GetWindowThreadProcessId(viewportHwnd, out var viewportPid);
            var inputTid = NativeMethods.GetWindowThreadProcessId(inputHwnd, out var inputPid);
            var viewportParent = WindowNative.GetParent(viewportHwnd);
            var inputParent = WindowNative.GetParent(inputHwnd);

            Check("child-hierarchy",
                viewportHwnd != IntPtr.Zero && inputHwnd != IntPtr.Zero
                    && inputHwnd != hwnd && viewportHwnd != hwnd && inputHwnd != viewportHwnd
                    && viewportParent == hwnd && inputParent == viewportHwnd,
                $"expected root->viewport->input distinct chain; "
                    + $"actual root=0x{hwnd.ToInt64():X} viewport=0x{viewportHwnd.ToInt64():X} "
                    + $"(parent=0x{viewportParent.ToInt64():X}) "
                    + $"input=0x{inputHwnd.ToInt64():X} (parent=0x{inputParent.ToInt64():X})");

            var getWndHr = store.GetWnd(1, out var storeWnd);
            Check("store-getwnd-is-input",
                getWndHr == TsfHResult.S_OK && storeWnd == inputHwnd,
                $"expected store.GetWnd(1)=input 0x{inputHwnd.ToInt64():X}; "
                    + $"actual hr={TsfHResult.Describe(getWndHr)} hwnd=0x{storeWnd.ToInt64():X}");

            Check("tsf-associated-hwnd-is-input",
                session.AssociatedHwnd == inputHwnd && inputHwnd != hwnd,
                $"expected AssociateFocus hwnd=input 0x{inputHwnd.ToInt64():X} "
                    + $"document_mgr={(session.DocumentManager is null ? "(null)" : "live")}; "
                    + $"actual associated=0x{session.AssociatedHwnd.ToInt64():X}");

            Check("window-owners",
                rootPid == Environment.ProcessId && viewportPid == rootPid && inputPid == rootPid
                    && rootTid == ownWin32Thread && viewportTid == ownWin32Thread
                    && inputTid == ownWin32Thread,
                $"expected distinct hwnds, same pid={Environment.ProcessId} same tid={ownWin32Thread}; "
                    + $"actual root pid/tid={rootPid}/{rootTid} "
                    + $"viewport pid/tid={viewportPid}/{viewportTid} "
                    + $"input pid/tid={inputPid}/{inputTid}");

            Check("child-visibility",
                !NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsWindowVisible(viewportHwnd)
                    && !NativeMethods.IsWindowVisible(inputHwnd),
                $"expected all hidden; actual root={NativeMethods.IsWindowVisible(hwnd)} "
                    + $"viewport={NativeMethods.IsWindowVisible(viewportHwnd)} "
                    + $"input={NativeMethods.IsWindowVisible(inputHwnd)}");

            var focusNow = NativeMethods.GetFocus();
            Check("no-focus-own-window",
                focusNow != hwnd && focusNow != viewportHwnd && focusNow != inputHwnd,
                $"expected focus nowhere in chain; actual focus=0x{focusNow.ToInt64():X} "
                    + $"input_focus_events={window.InputFocusEventCount}");

            trace("hidden-host", "child-topology", new Dictionary<string, object?>
            {
                ["child_input_surface"] = true,
                ["expected_chain"] = "root->viewport->input (distinct, same pid/tid)",
                ["expected_getwnd"] = $"0x{inputHwnd.ToInt64():X}",
                ["expected_associated_hwnd"] = $"0x{inputHwnd.ToInt64():X}",
                ["root_hwnd"] = $"0x{hwnd.ToInt64():X}",
                ["viewport_hwnd"] = $"0x{viewportHwnd.ToInt64():X}",
                ["input_hwnd"] = $"0x{inputHwnd.ToInt64():X}",
                ["root_owner"] = $"pid={rootPid} tid={rootTid}",
                ["viewport_owner"] = $"pid={viewportPid} tid={viewportTid}",
                ["input_owner"] = $"pid={inputPid} tid={inputTid}",
                ["root_visible"] = NativeMethods.IsWindowVisible(hwnd),
                ["viewport_visible"] = NativeMethods.IsWindowVisible(viewportHwnd),
                ["input_visible"] = NativeMethods.IsWindowVisible(inputHwnd),
                ["store_getwnd_hresult"] = TsfHResult.Describe(getWndHr),
                ["store_getwnd_hwnd"] = $"0x{storeWnd.ToInt64():X}",
                ["associated_hwnd_actual"] = $"0x{session.AssociatedHwnd.ToInt64():X}",
                ["focus_hwnd"] = $"0x{focusNow.ToInt64():X}",
                ["input_focus_events"] = window.InputFocusEventCount,
                ["show_window_calls"] = window.ShowWindowCalls,
            });
        }

        // R26：激活方式必须可辨；显式开启时还须记录真实 active flags 含 UIElement-only 位。
        if (probeContextView || traceLayoutChain || session.UiElementEnabledOnly)
        {
            Check("activation-mode", session.ActivationMode is "activate" or "activate-ex",
                $"mode={session.ActivationMode} uielement_enabled_only={session.UiElementEnabledOnly}");
        }

        if (session.UiElementEnabledOnly)
        {
            var flags = session.ReadActiveActivationFlags("hidden-host-check");
            var hasFlag = flags is { } value
                && (value & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) != 0;
            Check("uielement-enabled-only-active", hasFlag,
                $"flags={(flags is { } f ? $"0x{f:X}" : "(unavailable)")} mode={session.ActivationMode}");
        }
        Check("store-sink-advised", store.HasSink && store.SinkMask == TsfFlags.TS_AS_ALL_SINKS,
            $"has_sink={store.HasSink} mask=0x{store.SinkMask:X}");

        var state = session.ResourceState;
        Check("session-resources",
            state.Activated && state.ContextPushed && state.Associated && state.FocusSet && state.UiAdvised,
            state.ToString());
        Check("host-owns-com-reference", !state.ComInitialized,
            $"session_com_initialized={state.ComInitialized} owns_reference={apartment.OwnsReference}");

        // 真实消息泵：让 TSF 回调、布局通知与延迟的 store 通知在宿主线程上完成。
        window.RequestRelayout("hidden-host");
        PumpHostMessages(store, 400);
        Check("layout-published", window.LastLayout is not null,
            $"generation={store.LayoutGeneration} document_version={store.Document.Version}");
        Check("no-composition", !store.IsComposing, $"composing={store.IsComposing}");

        // 布局调用链诊断（默认关闭）：在隐藏宿主的安全阶段记录配对通知与只读焦点快照。
        // 默认关闭时不执行任何额外原生查询，也不改变任何既有检查。
        if (traceLayoutChain)
        {
            // R29B：own_hwnd 一律是真实输入 HWND（opt-in 为 input child，默认 off 即 root），
            // 隐藏路径绝不做焦点初始化/恢复——只读快照如实反映输入 identity。
            FocusSnapshot.Capture(store.Chain, trace, "hidden-host", window.InputHwnd,
                session.ThreadManager, session.DocumentManager, session.Context);
            Check("layout-chain-enabled", store.Chain.Enabled,
                $"enabled={store.Chain.Enabled} scope={store.Chain.DescribeScope()}");
        }
        else
        {
            Check("layout-chain-disabled", !store.Chain.Enabled,
                $"enabled={store.Chain.Enabled}");
        }

        // context/view 只读几何对照（默认关闭）：用空文档真实接线验证 view/store 查询路径，
        // 不输入、不改文档/选区/组合。拒绝、范围不一致或会话未执行都如实记为检查失败，不当作通过；
        // 只有 R20 定义的全部一致（status=ok）才算到达 view 层。
        string? probeStatus = null;
        if (probeContextView)
        {
            var span = store.CompositionSpan;
            var selection = store.Document.Selection;
            var probe = ContextViewProbe.RunIfEnabled(true, "hidden-host", session.Context,
                session.ClientId, store, trace,
                ContextViewProbe.ResolveRange(store.IsComposing, span.Start, span.End, selection.End));
            probeStatus = probe?.Status;
            var reachedViewLayer = probe is { Status: ContextViewProbeStatus.Ok };
            Check("context-view-probe", reachedViewLayer,
                probe is null
                    ? "(not enabled)"
                    : $"{probe.Value.Status} request={TsfHResult.Describe(probe.Value.RequestHResult)} "
                        + $"session={TsfHResult.Describe(probe.Value.SessionHResult)} "
                        + $"session_ran={probe.Value.SessionRan} range_match={probe.Value.RangeMatch} "
                        + $"rect_match={probe.Value.RectMatch} screen_match={probe.Value.ScreenMatch} "
                        + $"wnd_match={probe.Value.WndMatch}");
        }

        session.Cleanup();
        Check("cleanup-no-failures", session.CleanupFailures == 0, $"failures={session.CleanupFailures}");
        var after = session.ResourceState;
        Check("resources-released",
            !after.ComInitialized && !after.HasThreadManager && !after.HasDocumentManager && !after.HasContext,
            after.ToString());
        Check("store-sink-removed", !store.HasSink, $"has_sink={store.HasSink}");

        var aliveBeforeDispose = window.IsAlive;
        window.Dispose();
        Check("window-created-and-destroyed", aliveBeforeDispose && !window.IsAlive,
            $"alive_before={aliveBeforeDispose} alive_after={window.IsAlive}");
        Check("class-unregistered", !window.IsClassRegistered, $"registered={window.IsClassRegistered}");
        Check("no-binding-writes", recorder.Writes == 0, $"writes={recorder.Writes}");

        var exitCode = failed.Count == 0 ? 0 : 1;
        var detail = failed.Count == 0
            ? "hidden host startup path checks passed"
            : "failed checks: " + string.Join(",", failed);
        log.Record("host", "host", "hidden-host-result", new Dictionary<string, object?>
        {
            ["mode"] = "hidden-host",
            ["requested_apartment"] = HostApartment.DescribeRequested(apartment.RequestedSta),
            ["thread_selection"] = threadSelection.ToString(),
            ["runs_on_entry_thread"] = runsOnEntryThread,
            ["entry_managed_thread_id"] = EntryManagedThreadId,
            ["apartment_at_main_entry"] = ApartmentAtMainEntry,
            ["apartment_before"] = apartment.BeforeDescription,
            ["apartment"] = apartment.Description,
            ["apartment_type"] = apartment.ApartmentType,
            ["matches_requested"] = apartment.ReachedRequestedApartment,
            ["owns_com_reference"] = apartment.OwnsReference,
            ["win32_thread_id"] = ownWin32Thread,
            ["managed_thread_id"] = ownManagedThread,
            ["ui_thread_id"] = window.UiThreadId,
            ["show_window_calls"] = window.ShowWindowCalls,
            ["child_input_surface"] = childInputSurface,
            ["root_hwnd"] = $"0x{hwnd.ToInt64():X}",
            ["viewport_hwnd"] = $"0x{viewportHwnd.ToInt64():X}",
            ["input_hwnd"] = $"0x{inputHwnd.ToInt64():X}",
            ["scenario_ran"] = false,
            ["key_input_sent"] = false,
            ["foreground_requested"] = false,
            ["cleanup_failures"] = session.CleanupFailures,
            ["context_view_probe"] = probeStatus,
            ["trace_layout_chain"] = store.Chain.Enabled,
            ["checks_pass"] = failed.Count == 0,
            ["failed_checks"] = failed.Count == 0 ? null : string.Join(",", failed),
            ["candidate_visual"] = "not-required",
            ["assessment"] = failed.Count == 0 ? "pass" : "fail",
            ["pass"] = failed.Count == 0,
            ["exit_code"] = exitCode,
            ["detail"] = detail,
        });
        return exitCode;
    }

    /// <summary>在宿主线程上泵消息并送达延迟的 store 通知；不发键、不显示窗口。</summary>
    private static void PumpHostMessages(AcpTextStore store, int durationMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(1, durationMs);
        while (Environment.TickCount64 < deadline)
        {
            while (WindowNative.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, WindowNative.PM_REMOVE))
            {
                WindowNative.TranslateMessage(ref msg);
                WindowNative.DispatchMessageW(ref msg);
            }

            store.FlushDeferredNotifications();
            Thread.Sleep(10);
        }

        store.FlushDeferredNotifications();
    }

    internal static bool TryParse(string[] args, out ParsedArguments parsed, out string error)
    {
        parsed = default;
        error = string.Empty;
        var selfTest = false;
        var nativeSelfTest = false;
        var hiddenHost = false;
        var childInputSurface = false;
        string outDir = Path.Combine("artifacts", "ime-work", "ti07-tsf-prototype", "hostcheck");
        string? scenario = null;
        var timeoutMs = 300_000;
        var foregroundWaitMs = 180_000;
        var suppress = false;
        var probeContextView = false;
        var traceLayoutChain = false;
        var uiElementEnabledOnly = false;
        var childHideWhenClipped = false;
        var childSuspendTsfFocusWhenClipped = false;
        var hostDrawnCandidates = false;
        var verifyRestoredCommit = false;
        var scrollDelta = 7;
        var width = 760;
        var height = 420;
        bool? apartmentFilter = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--selftest":
                    selfTest = true;
                    break;
                case "--native-selftest":
                    nativeSelfTest = true;
                    break;
                case "--hidden-host":
                    hiddenHost = true;
                    break;
                case "--child-input-surface":
                    childInputSurface = true;
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, out outDir, out error))
                    {
                        return false;
                    }

                    break;
                case "--scenario":
                    if (!TryTakeValue(args, ref i, out var scenarioValue, out error))
                    {
                        return false;
                    }

                    scenario = scenarioValue;
                    break;
                case "--timeout-ms":
                    if (!TryTakeInt(args, ref i, out timeoutMs, out error) || timeoutMs <= 0)
                    {
                        error = error.Length > 0 ? error : "--timeout-ms must be positive";
                        return false;
                    }

                    break;
                case "--foreground-wait-ms":
                    if (!TryTakeInt(args, ref i, out foregroundWaitMs, out error) || foregroundWaitMs < 0)
                    {
                        error = error.Length > 0 ? error : "--foreground-wait-ms must be >= 0";
                        return false;
                    }

                    break;
                case "--suppress-candidate-ui":
                    suppress = true;
                    break;
                case "--probe-context-view":
                    probeContextView = true;
                    break;
                case "--trace-layout-chain":
                    traceLayoutChain = true;
                    break;
                case "--uielement-enabled-only":
                    uiElementEnabledOnly = true;
                    break;
                case "--child-hide-when-clipped":
                    childHideWhenClipped = true;
                    break;
                case "--child-suspend-tsf-focus-when-clipped":
                    childSuspendTsfFocusWhenClipped = true;
                    break;
                case "--host-drawn-candidates":
                    hostDrawnCandidates = true;
                    break;
                case "--verify-restored-commit":
                    verifyRestoredCommit = true;
                    break;
                case "--scroll-delta":
                    if (!TryTakeInt(args, ref i, out scrollDelta, out error))
                    {
                        return false;
                    }

                    break;
                case "--width":
                    if (!TryTakeInt(args, ref i, out width, out error) || width < 320)
                    {
                        error = error.Length > 0 ? error : "--width must be >= 320";
                        return false;
                    }

                    break;
                case "--height":
                    if (!TryTakeInt(args, ref i, out height, out error) || height < 240)
                    {
                        error = error.Length > 0 ? error : "--height must be >= 240";
                        return false;
                    }

                    break;
                case "--apartment":
                    if (!TryTakeValue(args, ref i, out var apartment, out error))
                    {
                        return false;
                    }

                    switch (apartment.ToLowerInvariant())
                    {
                        case "mta":
                            apartmentFilter = false;
                            break;
                        case "sta":
                            apartmentFilter = true;
                            break;
                        default:
                            error = "--apartment must be mta or sta.";
                            return false;
                    }

                    break;
                case "--help":
                case "-h":
                    parsed = new ParsedArguments(false, false, true, null, null, null);
                    return true;
                default:
                    error = $"Unknown argument '{arg}'.";
                    return false;
            }
        }

        if (hiddenHost && scenario is not null)
        {
            // 隐藏宿主回归不跑任何真实输入场景：两者同时指定是用法冲突，如实拒绝而不是静默忽略其一。
            error = "--hidden-host does not accept --scenario; run them separately.";
            return false;
        }

        if (hiddenHost && (selfTest || nativeSelfTest))
        {
            error = "--hidden-host cannot be combined with --selftest/--native-selftest.";
            return false;
        }

        if (childInputSurface)
        {
            // R29B 当前步只接线“真实输入 child 的创建 + TSF 关联 + hidden-host 回归”；
            // 可见几何执行器尚未接入——所有真实输入场景在本步一律如实拒绝（不静默降级
            // 为 root 输入 surface，也不假装 child 场景已可运行）。下一步移除该拒绝。
            if (selfTest || nativeSelfTest)
            {
                error = "--child-input-surface cannot be combined with --selftest/--native-selftest.";
                return false;
            }

            if (suppress)
            {
                error = "--child-input-surface does not accept --suppress-candidate-ui "
                    + "(candidate suppression is out of scope for the child input surface).";
                return false;
            }

            if (uiElementEnabledOnly)
            {
                error = "--child-input-surface does not accept --uielement-enabled-only "
                    + "(UIElement-only activation is out of scope for the child input surface).";
                return false;
            }

            if (probeContextView)
            {
                error = "--child-input-surface does not accept --probe-context-view "
                    + "(context/view probing is out of scope for the child input surface).";
                return false;
            }

            if (scenario is null && !hiddenHost)
            {
                error = "--child-input-surface requires --hidden-host or one explicit --scenario; "
                    + "the unqualified default scenario matrix is not supported.";
                return false;
            }

            if (scenario is not null
                && scenario is not "scroll-follow" and not "clip-out" and not "move-window")
            {
                // R29B 只接线 scroll-follow/clip-out/move-window 三个可见几何执行器；
                // 其余可见场景（组合/commit/cancel/resize/uielement/隐藏探针等）仍如实拒绝，
                // 不静默降级为 root 输入 surface，也不假装可运行。
                error = $"--child-input-surface only wires scroll-follow, clip-out and "
                    + $"move-window (scenario '{scenario}' is not wired).";
                return false;
            }
        }

        // R30：自有 input child 可见性实验只允许一个组合——--child-input-surface +
        // 显式 --scenario clip-out。默认/隐藏宿主/其他场景/冲突实验一律如实拒绝；
        // 本实验不改变既有路径（默认 off 时解析/行为逐字节不变）。
        if (childHideWhenClipped)
        {
            if (!childInputSurface)
            {
                error = "--child-hide-when-clipped requires --child-input-surface.";
                return false;
            }

            if (hiddenHost)
            {
                error = "--child-hide-when-clipped is not allowed with --hidden-host "
                    + "(hidden runs never show or focus the input surface).";
                return false;
            }

            if (scenario != "clip-out")
            {
                error = "--child-hide-when-clipped only supports --scenario clip-out "
                    + $"(got '{scenario ?? "(none)"}').";
                return false;
            }
        }

        // R31：自有 TSF 文档焦点挂起实验只允许一个组合——--child-input-surface +
        // 显式 --scenario clip-out + STA（与 R30 互斥，执行器构造时再兜一次）。
        // 默认/隐藏宿主/其他场景/冲突实验一律如实拒绝；默认 off 时既有路径不变。
        if (childSuspendTsfFocusWhenClipped)
        {
            if (childHideWhenClipped)
            {
                error = "--child-suspend-tsf-focus-when-clipped is mutually exclusive "
                    + "with --child-hide-when-clipped.";
                return false;
            }

            if (!childInputSurface)
            {
                error = "--child-suspend-tsf-focus-when-clipped requires --child-input-surface.";
                return false;
            }

            if (hiddenHost)
            {
                error = "--child-suspend-tsf-focus-when-clipped is not allowed with "
                    + "--hidden-host (hidden runs never engage a visible clipped state).";
                return false;
            }

            if (scenario != "clip-out")
            {
                error = "--child-suspend-tsf-focus-when-clipped only supports "
                    + $"--scenario clip-out (got '{scenario ?? "(none)"}').";
                return false;
            }

            if (apartmentFilter != true)
            {
                error = "--child-suspend-tsf-focus-when-clipped requires --apartment sta "
                    + "(TSF document focus ops must run on the owning STA UI thread).";
                return false;
            }
        }

        // R34：自有 GDI 候选呈现器（默认 off）。只允许一个组合——--child-input-surface +
        // 显式 --scenario scroll-follow|clip-out + STA。本模式内部自行配置
        // UiElementEnabledOnly 激活与候选专属 BeginUIElement 抑制，因此与显式的
        // R26/抑制/R30/R31 标志一律冲突拒绝——绝不把"内部配置"悄悄叠加到用户标志上。
        if (hostDrawnCandidates)
        {
            if (!childInputSurface)
            {
                error = "--host-drawn-candidates requires --child-input-surface "
                    + "(the overlay anchors on the real input child surface).";
                return false;
            }

            if (hiddenHost)
            {
                error = "--host-drawn-candidates is not allowed with --hidden-host "
                    + "(hidden runs never paint a candidate overlay).";
                return false;
            }

            if (selfTest || nativeSelfTest)
            {
                error = "--host-drawn-candidates cannot be combined with "
                    + "--selftest/--native-selftest.";
                return false;
            }

            if (scenario is not "scroll-follow" and not "clip-out")
            {
                error = "--host-drawn-candidates only supports --scenario scroll-follow "
                    + $"or clip-out (got '{scenario ?? "(none)"}').";
                return false;
            }

            if (apartmentFilter != true)
            {
                error = "--host-drawn-candidates requires --apartment sta "
                    + "(UIElement sink callbacks and the overlay run on the owning STA UI thread).";
                return false;
            }

            if (childHideWhenClipped || childSuspendTsfFocusWhenClipped)
            {
                error = "--host-drawn-candidates is mutually exclusive with "
                    + "--child-hide-when-clipped/--child-suspend-tsf-focus-when-clipped "
                    + "(the overlay owns the clipped-restore presentation path).";
                return false;
            }

            if (suppress)
            {
                error = "--host-drawn-candidates conflicts with --suppress-candidate-ui "
                    + "(this mode configures its own candidate-only BeginUIElement suppression).";
                return false;
            }

            if (uiElementEnabledOnly)
            {
                error = "--host-drawn-candidates conflicts with --uielement-enabled-only "
                    + "(this mode configures UIElement-only activation internally).";
                return false;
            }

            if (probeContextView || traceLayoutChain)
            {
                error = "--host-drawn-candidates does not accept "
                    + "--probe-context-view/--trace-layout-chain diagnostics.";
                return false;
            }
        }

        // R34：本模式自管激活与抑制——UiElementEnabledOnly 在这里内部开启，
        // 不需要、也不允许用户重复显式传（上面的冲突检查已拒绝）。
        if (hostDrawnCandidates)
        {
            uiElementEnabledOnly = true;
        }

        // R37：恢复后提交证明只允许一个组合——--host-drawn-candidates +
        // --child-input-surface + 显式 --scenario clip-out + --apartment sta。它证明的是
        // "裁剪隐藏→恢复的当前候选能否真的提交"，因此任何其他场景/缺少宿主自绘候选/
        // 隐藏宿主/自检/冲突实验一律在解析期如实拒绝；默认 off 时 R36 证明路径不变。
        if (verifyRestoredCommit)
        {
            if (!hostDrawnCandidates)
            {
                error = "--verify-restored-commit requires --host-drawn-candidates "
                    + "(the proof commits the restored host-drawn candidate).";
                return false;
            }

            if (!childInputSurface)
            {
                error = "--verify-restored-commit requires --child-input-surface.";
                return false;
            }

            if (hiddenHost)
            {
                error = "--verify-restored-commit is not allowed with --hidden-host "
                    + "(hidden runs never show or commit a candidate overlay).";
                return false;
            }

            if (selfTest || nativeSelfTest)
            {
                error = "--verify-restored-commit cannot be combined with "
                    + "--selftest/--native-selftest.";
                return false;
            }

            if (scenario != "clip-out")
            {
                error = "--verify-restored-commit only supports --scenario clip-out "
                    + $"(got '{scenario ?? "(none)"}').";
                return false;
            }

            if (apartmentFilter != true)
            {
                error = "--verify-restored-commit requires --apartment sta "
                    + "(the commit keystroke and TSF callbacks run on the owning STA UI thread).";
                return false;
            }

            if (childHideWhenClipped || childSuspendTsfFocusWhenClipped)
            {
                error = "--verify-restored-commit is mutually exclusive with "
                    + "--child-hide-when-clipped/--child-suspend-tsf-focus-when-clipped "
                    + "(the overlay owns the clipped-restore presentation path).";
                return false;
            }

            if (suppress || probeContextView || traceLayoutChain)
            {
                error = "--verify-restored-commit does not accept "
                    + "--suppress-candidate-ui/--probe-context-view/--trace-layout-chain.";
                return false;
            }
        }

        parsed = new ParsedArguments(selfTest, nativeSelfTest, false, scenario, new ScenarioOptions
        {
            OutDir = Path.GetFullPath(outDir),
            Scenario = scenario,
            TimeoutMs = timeoutMs,
            ForegroundWaitMs = foregroundWaitMs,
            SuppressCandidateUi = suppress,
            ScrollDelta = scrollDelta,
            WindowWidth = width,
            WindowHeight = height,
            Sta = apartmentFilter ?? false,
            HiddenHost = hiddenHost,
            ProbeContextView = probeContextView,
            TraceLayoutChain = traceLayoutChain,
            UiElementEnabledOnly = uiElementEnabledOnly,
            ChildInputSurface = childInputSurface,
            ChildHideWhenClipped = childHideWhenClipped,
            ChildSuspendTsfFocusWhenClipped = childSuspendTsfFocusWhenClipped,
            HostDrawnCandidates = hostDrawnCandidates,
            VerifyRestoredCommit = verifyRestoredCommit,
        }, apartmentFilter);
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value, out string error)
    {
        error = string.Empty;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            error = $"Missing value for '{args[index]}'.";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool TryTakeInt(string[] args, ref int index, out int value, out string error)
    {
        value = 0;
        if (!TryTakeValue(args, ref index, out var text, out error))
        {
            return false;
        }

        if (!int.TryParse(text, out value))
        {
            error = $"Invalid integer '{text}' for '{args[index - 1]}'.";
            return false;
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            CSharp.UI.TsfHostCheck — isolated TSF feasibility prototype (TI-07 step 1)

            Usage:
              dotnet run --project tools/CSharp.UI.TsfHostCheck -- [options]

            Options:
              --selftest                 Run pure managed self-checks; no window, no COM, no input.
              --native-selftest          Real hidden-window + real TSF context smoke (MTA and STA on
                                         separate threads by default). Never shows a window or injects keys.
              --hidden-host              Real host startup-path regression: same apartment/window/TSF/
                                         message-loop/cleanup path as the visible run, but the window is
                                         never shown, no foreground is requested, no key is sent and no
                                         scenario runs. One apartment per process; exit 0/1/2.
              --child-input-surface      R29B opt-in real single-line input child: a
                                         root->viewport->input child hierarchy owns TSF
                                         association, GetWnd, drawing, key/char routing and
                                         focus instead of the root. Accepted with --hidden-host
                                         (real child TSF wiring regression; still zero
                                         show/focus/input) and with exactly three explicit
                                         visible scenarios: scroll-follow, clip-out and
                                         move-window, which run through the shared child
                                         geometry executor (safepoint + invariant + monitor
                                         evidence + exact-restore). Every other visible
                                         scenario is refused with an explicit error, and the
                                         unqualified default matrix and
                                         --suppress-candidate-ui/--uielement-enabled-only/
                                         --probe-context-view are also refused. Default off:
                                         without it the root stays the input surface.
              --child-hide-when-clipped   R30 bounded experiment (default off): only with
                                         --child-input-surface --scenario clip-out. After the
                                         strict zero-geometry verification and before the
                                         clipped capture, hides the own input child with a
                                         NOACTIVATE SetWindowPos and shows it again during
                                         restore. If native focus/composition/foreground facts
                                         change, the run fails honestly — no SetFocus, no TSF
                                         re-association, no re-type. Hidden-host, the default
                                         matrix and every other scenario/flag are refused.
              --child-suspend-tsf-focus-when-clipped
                                         R31 bounded experiment (default off): only with
                                         --child-input-surface --scenario clip-out
                                         --apartment sta. After the strict zero-geometry
                                         verification and before the clipped capture,
                                         suspends the own TSF document focus with a real
                                         ITfThreadMgr.SetFocus(null), and restores the
                                         same saved doc/context with SetFocus during
                                         restore. Win32 foreground/focus/association stay
                                         untouched — no HWND SetFocus/AssociateFocus/
                                         Pop/Push, no cancel/commit/re-type. Mutually
                                         exclusive with --child-hide-when-clipped;
                                         hidden-host, the default matrix and every other
                                         scenario/flag are refused.
              --host-drawn-candidates   R34 bounded experiment (default off): only with
                                         --child-input-surface --scenario scroll-follow|clip-out
                                         --apartment sta. Declines the TIP's own candidate UI
                                         (candidate-only BeginUIElement -> pbShow=FALSE under
                                         UIElementEnabledOnly activation, configured internally)
                                         and paints the real ITfCandidateListUIElement snapshot
                                         in an own root-client GDI overlay: never calls Show(TRUE),
                                         never touches a foreign candidate HWND, never retypes or
                                         SetFocus(null)s to restore. Fully-clipped composition hides
                                         the overlay; restore re-displays the same live snapshot and
                                         element generation. Baseline requires a genuine non-empty
                                         candidate list; visual stays pending for human review
                                         (exit 3). Conflicts with --child-hide-when-clipped,
                                         --child-suspend-tsf-focus-when-clipped,
                                         --suppress-candidate-ui and --uielement-enabled-only.
              --verify-restored-commit   R37 bounded experiment (default off): only with
                                         --host-drawn-candidates --child-input-surface
                                         --scenario clip-out --apartment sta. After every
                                         restore evidence capture succeeded and before the
                                         composition cleanup, proves the *restored* candidate
                                         can actually commit: a strict pre safe point plus the
                                         same presenter snapshot must show the selected text
                                         '你好' with a valid generation, zero binding writes and
                                         an unchanged composition; then one real Space is sent
                                         through the existing BeforeSendGuard (every down/up
                                         guarded). The scenario passes only if the composition
                                         ends exactly once, the document text is exactly
                                         '你好', the binding delta is exactly 1 with entry text
                                         '你好' and source tsf-composition, begin/session are
                                         unchanged, and the presenter is cleared with the real
                                         overlay not visible. No forced focus, no retype, no
                                         cancel. Default off preserves the R36 proof path.
              --apartment mta|sta        Host-thread COM apartment, decided before any HWND/COM is
                                         created. Default MTA. STA is opt-in. For --native-selftest,
                                         omit to run both.
              --out <dir>                Evidence directory (JSONL + PNG). Default:
                                         artifacts/ime-work/ti07-tsf-prototype/hostcheck
              --scenario <name>          Run one scenario. Known: nihao-commit, escape-cancel,
                                         enter-select, scroll-follow, move-window, resize, clip-out,
                                         blur-close, uielement-hide-probe, native-ui-lifecycle,
                                         ascii-plain, unicode-direct.
                                         Unknown names exit 1. Conflicts with --hidden-host.
              native-ui-lifecycle        R27 single-variable comparison: suppress the candidate at
                                         BeginUIElement, wait for a *live* candidate element, then
                                         call ITfUIElement::Show(TRUE) / Show(FALSE) / Show(TRUE) on
                                         that same live element with a screenshot per stage. Only
                                         candidate elements are touched (language bar and other
                                         elements are skipped). No custom drawing, no candidate HWND
                                         access, no profile/focus change, no extra input. A successful
                                         Show does NOT prove the image is visible: the candidate
                                         visual stays pending for human review (exit 3).
              --timeout-ms <n>           Whole-run watchdog. Default 300000.
              --foreground-wait-ms <n>   Wait for the user to click this window. Default 180000.
              --suppress-candidate-ui    clip-out only: decline/hide TSF candidate UI elements.
              --probe-context-view       Diagnostic (default off): before/after the scroll-follow,
                                         move-window and clip-out screenshots, run a real read-only
                                         edit session (TF_ES_SYNC|TF_ES_READ) and compare
                                         ITfContextView::GetTextExt/GetScreenExt/GetWnd with the
                                         store geometry for the same temporary ACP range. The
                                         status is ok only when the session actually ran, the
                                         temporary range extent equals the requested range exactly,
                                         and the text rect/clipped, screen rect and HWND all match;
                                         a failed extent query stops the probe (range-mismatch /
                                         refused are reported, never used as geometry evidence).
                                         Read-only: never writes text, selection, composition or
                                         Binding. Also usable with --hidden-host on the empty document.
              --trace-layout-chain       Diagnostic (default off): add paired OnLayoutChange enter/exit
                                         IDs, RequestLock enter/return with *phrSession, and
                                         scope attribution (request-session / view-query /
                                         store-direct-query / tsf-callback / unknown) to the
                                         existing trace, plus read-only Win32/TSF focus snapshots at
                                         host safe points. Never changes lock logic, notification
                                         order or geometry results; no native focus query runs
                                         while disabled.
              --uielement-enabled-only    Activation comparison (default off). Instead of the
                                         default ITfThreadMgr::Activate, use
                                         ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)
                                         (flag 0x4). pbShow stays TRUE, so the TIP may still draw
                                         its own UI; nothing is suppressed and no candidate HWND
                                         is touched. QI/ActivateEx/GetActiveFlags HRESULTs and the
                                         real active flags are logged; unsupported QI, a failed
                                         call, or missing flag bits fail explicitly with no
                                         fallback to the default activation. This only switches the
                                         activation mode — it does not promise candidate following.
              --scroll-delta <px>        scroll-follow delta. Default 7.
              --width <px> --height <px> Initial window size. Default 760x420.

            Exit codes:
              0 = all checks passed (no pending visual assessment)
              1 = assertion failure / unknown scenario / hidden-host check failure
              2 = precondition missing (apartment, window, or TSF engage failed)
              3 = script checks passed but candidate visual still needs human review

            Safety:
              * Only the own window/thread/COM resources are touched.
              * One normal foreground request, then wait for a real user click; no TOPMOST,
                no AttachThreadInput, no simulated mouse.
              * Every SendInput is preceded by GetForegroundWindow()==own window.
              * No global IME/default/profile changes, no synthetic WM_IME_* messages,
                no moving/hiding the system candidate HWND.
            """);
    }

    internal readonly record struct ParsedArguments(
        bool SelfTest, bool NativeSelfTest, bool Help, string? Scenario,
        ScenarioOptions? Options, bool? ApartmentFilter);
}
