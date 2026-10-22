using System.Globalization;
using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.ImeProbe;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;

namespace CSharp.UI.ImeHostCheck;

/// <summary>
/// TI-07 生产宿主真实 IME 验收工具：在真实 <c>UiApplication</c>（OpenTK 宿主 + IME 桥）窗口里
/// 用 <c>SendInput</c> 物理/扫描码按键驱动已安装的输入法（默认当前活动 profile；--profile mspinyin
/// 以 FORPROCESS 范围临时选择已安装的微软拼音并在结束时恢复，绝不改用户全局默认输入法）。
/// </summary>
/// <remarks>
/// <para>
/// 安全边界与 TI-01 探针一致：只在确认本工具自有窗口持有前台时才发送按键；前台丢失立即中止；
/// 截图只抓本窗口矩形。本工具不合成 WM_IME_* 消息，也不读取其他窗口的输入。
/// </para>
/// <para>
/// 证据判据：真实 WM_IME_* 消息序列（生产桥 Trace）、绑定 setter 次数与最终文本（每次提交恰好写一次）、
/// 锚点发布/撤销计数与客户区坐标、子类化恢复与回调异常计数。API 返回值单独不算候选窗可见/跟随的证据。
/// </para>
/// <para>
/// 用法：<c>imehostcheck --out &lt;dir&gt; [--timeout-ms 60000] [--profile mspinyin]</c>。
/// 退出码：0=全部场景通过；2=前置条件缺失（非 Windows / 无前台 / 桥未安装 / 非 IME profile）；
/// 1=有失败场景。无前台/非 IME 时相关场景标 unverified 而不是失败？——不：诚实标 failure，
/// 由报告按 reason 区分“环境不可用”与“实现缺陷”。
/// </para>
/// </remarks>
internal static class Program
{
    private const ushort VkEscape = 0x1B;
    private const ushort VkReturn = 0x0D;
    private const ushort VkSpace = 0x20;
    private const ushort VkD1 = 0x31;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkZ = 0x5A;
    private const ushort VkY = 0x59;
    private const int VkF10 = 0x79;
    private const uint WmClose = 0x0010;

    /// <summary>全部场景名；--scenario 过滤器必须命中其一，否则明确报错（不允许 0 场景静默通过）。</summary>
    private static readonly string[] AllScenarios =
    {
        "nihao-commit", "escape-cancel", "enter-select", "segmented",
        "ascii-plain", "digit-select", "unicode-direct", "focus-switch-cancel",
        "container-scroll", "breakpoint-reflow", "anchor-follow",
        "minimize-restore", "hide-restore", "window-deactivate", "horizontal-scroll", "disabled-unmount", "resize-samebranch",
        "external-update-cancel", "undo-redo", "close-window",
        // 诊断场景：只在显式 --scenario 指定时运行，不进默认矩阵。
        "candidate-visibility-probe",
        "native-caret-probe",
        "native-caret-early-probe",
        "scroll-edit-diagnostic",
        "ime-control-position-probe",
    };
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("imehostcheck 只能在 Windows 上运行（IME 桥是 Windows 原生路径）。");
            return 2;
        }

        var outDir = Path.GetFullPath(GetArg(args, "--out") ?? "artifacts/ime-work/ti07/hostcheck");
        var timeoutMs = int.Parse(GetArg(args, "--timeout-ms") ?? "300000", CultureInfo.InvariantCulture);
        var foregroundWaitMs = int.Parse(GetArg(args, "--foreground-wait-ms") ?? "180000", CultureInfo.InvariantCulture);
        var manualWaitMs = int.Parse(GetArg(args, "--manual-wait-ms") ?? "90000", CultureInfo.InvariantCulture);
        var profileName = GetArg(args, "--profile");
        var useTsf = args.Any(a => a == "--tsf");
        // --scenario <name>：只跑指定场景（环境/会话建立仍照常，结尾仍走关闭/清理）。
        var scenarioFilter = GetArg(args, "--scenario");
        // --manual-english：只跑 ascii-plain，中英文切换完全交给用户手动完成（R16：微软拼音对
        // IMM 转换位与注入 Shift 均不响应时，可信路径是真实用户切换）。程序不写任何 IME 模式位。
        var manualEnglish = args.Any(a => a == "--manual-english");
        // --english-keyboard：ascii-plain 改用“既有已启用英文键盘布局 profile”FORPROCESS 选择并恢复
        // （TF_PROFILETYPE_KEYBOARDLAYOUT，HKL 0x00000409；本轮先 GetProfile 核对 TF_IPP_FLAG_ENABLED
        // 才切换）。这是真实扫描码英文输入，但不是微软拼音内部英文模式，日志如实标注。
        var englishKeyboard = args.Any(a => a == "--english-keyboard");
        if (manualEnglish)
        {
            if (scenarioFilter is not null
                && !string.Equals(scenarioFilter, "ascii-plain", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("--manual-english 只覆盖 ascii-plain；指定的 --scenario 被忽略。");
            }

            scenarioFilter = "ascii-plain";
        }
        Directory.CreateDirectory(outDir);
        var logPath = Path.Combine(outDir, "ime-host-check.jsonl");
        using var log = new ProbeLog(logPath);

        log.Record("env", "tool", "start", new Dictionary<string, object?>
        {
            ["os"] = Environment.OSVersion.ToString(),
            ["arch"] = Environment.Is64BitProcess ? "x64" : "x86",
            ["pid"] = Environment.ProcessId,
            ["main_tid"] = Environment.CurrentManagedThreadId,
            ["ui_thread_apartment"] = Thread.CurrentThread.GetApartmentState().ToString(),
            ["coget_apartment_type"] = DescribeApartment(),
            ["note"] = "UI 主循环运行于带 [STAThread] 的现有主线程；此项记录实际 apartment，不创建或迁移线程。",
            ["out"] = outDir,
            ["timeout_ms"] = timeoutMs,
            ["foreground_wait_ms"] = foregroundWaitMs,
            ["manual_wait_ms"] = manualWaitMs,
            ["manual_english"] = manualEnglish,
            ["tsf_opt_in"] = useTsf,
            ["scenario_filter"] = scenarioFilter ?? "(all)",
        });

        // 过滤器自检：未知场景名立刻报错退出（不启动宿主、不允许 0 场景记为通过）。
        if (scenarioFilter is { } filter
            && !AllScenarios.Any(n => string.Equals(n, filter, StringComparison.OrdinalIgnoreCase)))
        {
            log.Record("script", "tool", "unknown-scenario", new Dictionary<string, object?>
            {
                ["requested"] = scenarioFilter,
                ["known"] = string.Join("|", AllScenarios),
            });
            Console.Error.WriteLine($"未知场景 '{scenarioFilter}'。可选：{string.Join(", ", AllScenarios)}");
            return 1;
        }

        // --selftest：无 GUI 自检（确认守卫语义等纯逻辑），不启动宿主、不发送任何输入。
        if (args.Any(a => a == "--selftest"))
        {
            return SelfTest(log) ? 0 : 1;
        }

        // 绑定见证：提交断言靠真实 setter 调用次数 + 最终文本。第二字段用于焦点切换取消矩阵。
        // 布局是真实 ScrollView + Responsive：内容高于视口可滚，宽度越过 460 逻辑像素断点时
        // 两个字段在纵向/横向排列间切换——覆盖滚动与断点重排下的锚点矩阵。
        var text = string.Empty;
        var writes = 0;
        // 验收工具专用时序见证：每次真实 setter 落一条 trace（序号/文本/写数/当前阶段标签），
        // 用于把“隐藏/失活期间的提交写”与当时动作精确对位；仅工具日志，非生产遥测。
        var phase = "init";
        var binding = new Binding<string>(() => text, v =>
        {
            text = v;
            writes++;
            log.Record("script", "trace", "binding-write", new Dictionary<string, object?>
            {
                ["field"] = 1,
                ["write_seq"] = writes,
                ["text"] = v,
                ["phase"] = phase,
            });
        });
        var field = new TextField(binding) { Width = 360, Height = 40 };
        var text2 = string.Empty;
        var writes2 = 0;
        var binding2 = new Binding<string>(() => text2, v =>
        {
            text2 = v;
            writes2++;
            log.Record("script", "trace", "binding-write", new Dictionary<string, object?>
            {
                ["field"] = 2,
                ["write_seq"] = writes2,
                ["text"] = v,
                ["phase"] = phase,
            });
        });
        var field2 = new TextField(binding2) { Width = 360, Height = 40 };
        var layoutMode = "(not-built)";
        var harness = new HarnessComponent(field, field2, useTsf, m => layoutMode = m);
        var root = new ScrollView(harness, ScrollAxis.Vertical);

        UiHostContext? ctx = null;
        var failures = new List<string>();
        var envUnavailable = false;
        WindowsImeBridge? bridge = null;
        var hostExited = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();

        // 看门狗在主循环运行期间触发（不是等 Run 返回后才 Join）：到时取消脚本并请求宿主退出。
        // Timer 回调在线程池线程上运行；ctx.RequestExit 可从任意线程调用，断点安全。
        using var watchdog = new Timer(
            _ =>
            {
                cts.Cancel();
                try
                {
                    log.Record("env", "tool", "watchdog-fired", new Dictionary<string, object?>
                    {
                        ["timeout_ms"] = timeoutMs,
                        ["host_exited"] = hostExited.IsSet,
                    });
                }
                catch (Exception)
                {
                }

                try
                {
                    ctx?.RequestExit(2);
                }
                catch (Exception)
                {
                }
            },
            null, timeoutMs, Timeout.Infinite);

        var scenariosRan = 0;
        var worker = new Thread(() =>
        {
            try
            {
                var rr = RunScript(
                    log, outDir,
                    ctxAccessor: () => ctx,
                    bindingRead: () => (text, writes),
                    binding2Read: () => (text2, writes2),
                    externalWrite: v => { text = v; /* 外部更新不写 setter */ },
                    layoutModeRead: () => layoutMode,
                    harness,
                    setPhase: p => { phase = p; log.Record("script", "trace", "phase", new Dictionary<string, object?> { ["phase"] = p }); },
                    failures, profileName, timeoutMs, foregroundWaitMs, scenarioFilter,
                    manualEnglish, manualWaitMs, useTsf, englishKeyboard,
                    captureBridge: b => bridge = b,
                    hostExited, cts.Token);
                envUnavailable = rr.EnvUnavailable;
                scenariosRan = rr.ScenariosRan;
            }
            catch (OperationCanceledException)
            {
                failures.Add("script-cancelled-by-timeout");
            }
            catch (Exception ex)
            {
                log.Record("script", "tool", "script-fault", new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["detail"] = ex.Message,
                });
                failures.Add($"script-fault:{ex.GetType().Name}");
            }
            finally
            {
                // 脚本无论完成、环境不可用还是看门狗取消，都必须让宿主退出——不能留下挂起的窗口。
                // host exit code 与脚本判定同源：2=环境不可用，1=有失败，0=正常完成。
                try
                {
                    ctx?.RequestExit(ComputeExitCode(
                        envUnavailable, false, failures.Count, scenariosRan));
                }
                catch (Exception)
                {
                    // 宿主已经退出时忽略。
                }
            }
        })
        { IsBackground = true, Name = "imehostcheck-script" };
        worker.Start();

        var exitCode = 0;
        Exception? hostError = null;
        try
        {
            exitCode = UiApplication.Run(root, new UiApplicationOptions
            {
                Title = "CSharp.UI ImeHostCheck",
                Width = 480,
                Height = useTsf ? 640 : 140,
                Started = c => ctx = c,
                UseTsfInputBridge = useTsf,
            });
        }
        catch (Exception ex)
        {
            hostError = ex;
        }
        finally
        {
            hostExited.Set();
        }

        // 看门狗已在宿主运行期间强制退出；这里只给 worker 有限收尾时间。
        // worker 可能仍残留时如实记录——日志在进程退出前保持打开，worker 不写已关闭的 log。
        if (!worker.Join(15_000))
        {
            log.Record("env", "tool", "worker-stuck", new Dictionary<string, object?>
            {
                ["note"] = "看门狗取消后脚本线程仍未退出；进程退出将截断其尾部写入。",
            });
            failures.Add("worker-not-exited");
        }

        log.Record("env", "tool", "finish", new Dictionary<string, object?>
        {
            ["env_unavailable"] = envUnavailable,
            ["host_exit_code"] = hostError is null ? exitCode : -1,
            ["host_exception"] = hostError?.GetType().Name ?? "(none)",
            ["subclass_restored"] = bridge?.SubclassRestored,
            ["subclass_installed_final"] = bridge?.SubclassInstalled,
            ["callback_exceptions"] = bridge?.CallbackExceptionCount,
            ["restore_failures"] = bridge?.RestoreFailureCount,
            ["geometry_publishes"] = bridge?.GeometryPublishCount,
            ["geometry_revokes"] = bridge?.GeometryRevokeCount,
            ["final_text"] = text,
            ["binding_writes"] = writes,
            ["final_text2"] = text2,
            ["binding_writes2"] = writes2,
            ["failures"] = string.Join("|", failures),
        });
        Console.WriteLine($"log: {logPath}");
        // 退出码：0=全部场景通过；2=前置条件缺失（环境不可用）；1=有失败场景或宿主异常。
        return ComputeExitCode(envUnavailable, hostError is not null, failures.Count, scenariosRan);
    }

    // ------------------------------------------------------------------ 验收脚本

    /// <returns>前置条件缺失（环境不可用）时返回 true——宿主未启动/桥未安装/前台不可得。</returns>
    private static (bool EnvUnavailable, int ScenariosRan) RunScript(
        ProbeLog log,
        string outDir,
        Func<UiHostContext?> ctxAccessor,
        Func<(string Text, int Writes)> bindingRead,
        Func<(string Text, int Writes)> binding2Read,
        Action<string> externalWrite,
        Func<string> layoutModeRead,
        HarnessComponent harness,
        Action<string> setPhase,
        List<string> failures,
        string? profileName,
        int timeoutMs,
        int foregroundWaitMs,
        string? scenarioFilter,
        bool manualEnglish,
        int manualWaitMs,
        bool useTsf,
        bool englishKeyboard,
        Action<WindowsImeBridge?> captureBridge,
        ManualResetEventSlim hostExited,
        CancellationToken ct)
    {
        var ctx = WaitFor(log, "host-started", () => ctxAccessor(), 10_000);
        if (ctx is null)
        {
            failures.Add("host-not-started");
            return (true, 0);
        }

        var host = UiApplication.CurrentHost;
        var hwnd = host is null ? IntPtr.Zero : WindowsImeBridge.GetWindowHandle(host.Window);
        var bridge = host?.ImeBridge;
        var router = host?.Router;
        captureBridge(bridge);
        log.Record("env", "host", "input-backend", new Dictionary<string, object?>
        {
            ["tsf_opt_in"] = useTsf,
            ["backend"] = router?.ActiveBackend.ToString() ?? "(none)",
            ["reason"] = router?.BackendReason ?? "(none)",
            ["session_present"] = router?.TsfBridge?.Session is not null,
        });
        log.Record("env", "host", "window", new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{hwnd.ToInt64():X}",
            ["window_tid"] = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(hwnd, out _),
            ["window_dpi"] = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(hwnd),
            ["bridge_present"] = bridge is not null,
            ["bridge_install_error"] = bridge?.InstallError ?? "(none)",
            ["capabilities"] = bridge?.Capabilities.ToString() ?? "(no bridge)",
        });

        if (hwnd == IntPtr.Zero || bridge is null || !bridge.SubclassInstalled)
        {
            failures.Add("bridge-not-installed");
            return (true, 0);
        }

        bridge.Trace = line => log.Record("bridge", "ime-bridge", "trace",
            new Dictionary<string, object?> { ["line"] = line });

        // 前台归属：先只读检查；不在前台时只在 UI 线程做一次普通 Window.Focus()，然后
        // 等待用户点击自有窗口。不调用旧 InputScript.EnsureForeground——其内部 TOPMOST/
        // AttachThreadInput 与本工具安全边界冲突，且一次失败会把实例置为永久 aborted。
        // InputScript 实例在确认自有前台之后才创建，保证其每次 SendRaw 前的自有窗口检查生效。
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            log.Record("script", "host", "foreground-wait", new Dictionary<string, object?>
            {
                ["note"] = "已做一次普通 Focus；请点击本工具窗口使其成为前台；等待期间不发送任何按键。",
                ["wait_ms"] = foregroundWaitMs,
            });
            ctx.Dispatcher.Post(() => UiApplication.CurrentHost!.Window.Focus());
            if (!WaitForUserForeground(hwnd, foregroundWaitMs, ct))
            {
                failures.Add("foreground-unavailable");
                return (true, 0);
            }
        }

        var script = new InputScript(log, hwnd, "hostcheck") { UseScanCodes = true };
        log.Record("env", "host", "foreground-owned", new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{hwnd.ToInt64():X}",
        });

        // 真实 profile 身份必须在生产 UI 线程上核对（TSF 活动身份与线程/进程上下文相关，
        // 不能用脚本线程的读数冒充）。COM 状态如实记录：UI 线程已有 COM 时只读查询即可；
        // 没有 COM 时记录真实错误，绝不为通过检查而把宿主线程改成别的套间模型。
        var uiCom = Query(ctx, () =>
        {
            var apartment = DescribeApartment();
            var (hr, p) = TsfProfile.ReadActiveProfile(log, "hostcheck-ui-thread");
            return (Apartment: apartment, Hr: hr, Profile: p);
        }, ct);
        log.Record("env", "tsf", "ui-thread-com", new Dictionary<string, object?>
        {
            ["ui_thread_apartment"] = uiCom.Apartment,
            ["read_active_hr"] = $"0x{uiCom.Hr:X8}",
            ["ui_active_type"] = uiCom.Profile.ProfileTypeName,
            ["ui_active_clsid"] = uiCom.Profile.Clsid.ToString(),
            ["ui_active_profile"] = uiCom.Profile.ProfileGuid.ToString(),
            ["note"] = "生产 UI 线程的真实 COM/活动身份；失败如实记录，不在 UI 线程上初始化 COM。",
        });

        // 脚本线程只在自己初始化的 COM 上做同样只读核对，作为对照。
        var comHr = TsfProfile.InitializeCom();
        log.Record("env", "tsf", "script-thread-com", new Dictionary<string, object?>
        {
            ["coinitialize_hr"] = $"0x{comHr:X8}",
            ["note"] = "只在脚本线程初始化 COM；UI 主线程不受影响。",
        });
        var (profileHr, active) = TsfProfile.ReadActiveProfile(log, "hostcheck-before");
        log.Record("env", "tsf", "active-profile", new Dictionary<string, object?>
        {
            ["hr"] = $"0x{profileHr:X8}",
            ["type"] = active.ProfileTypeName,
            ["langid"] = $"0x{active.LangId:X4}",
            ["clsid"] = active.Clsid.ToString(),
            ["profile_guid"] = active.ProfileGuid.ToString(),
        });

        // --profile mspinyin：本轮微软拼音已安装且是活动输入法。只读核对实际活动身份；
        // 若活动项不是微软拼音，仅按 TF_IPPMF_FORPROCESS 选择已启用的它（S_FALSE=未启用即失败，
        // 进程退出激活即失效），结束时同样按 FORPROCESS 恢复原活动项——全程不触碰 ENABLED 标志，
        // 不使用 ENABLEPROFILE/DISABLEPROFILE，不改用户全局默认输入法。
        TsfProfile.InputProcessorProfile? restoreProfile = null;
        if (profileName == "mspinyin")
        {
            var alreadyActive = profileHr >= 0
                && active.ProfileType == TsfProfile.TfProfileTypeInputProcessor
                && active.Clsid == TsfProfile.MicrosoftPinyinClsid
                && active.ProfileGuid == TsfProfile.MicrosoftPinyinProfile;
            if (alreadyActive)
            {
                log.Record("script", "tsf", "profile-already-active", new Dictionary<string, object?>
                {
                    ["clsid"] = active.Clsid.ToString(),
                    ["profile_guid"] = active.ProfileGuid.ToString(),
                });
            }
            else
            {
                var (hr, detail) = TsfProfile.ActivateForProcess(
                    log, "hostcheck-mspinyin", TsfProfile.MicrosoftPinyinClsid,
                    TsfProfile.MicrosoftPinyinProfile, TsfProfile.LangIdChineseSimplified);
                if (hr != 0)
                {
                    log.Record("script", "tsf", "profile-unavailable", new Dictionary<string, object?>
                    {
                        ["reason"] = detail,
                    });
                    failures.Add("mspinyin-profile-unavailable");
                }
                else
                {
                    // 激活成功（ActivateForProcess 已回读实际身份核对一致）：
                    // 结束前把原活动项以同一 FORPROCESS 范围恢复（仅当它是 input processor）。
                    restoreProfile = active;
                }
            }
        }

        var ran = 0;
        try
        {
            var fieldNodes = Query(ctx, () => ctx.Runtime.Root.FindDescendants<TextField>().ToList(), ct);
            if (fieldNodes.Count < 2)
            {
                failures.Add("textfield-not-found");
                return (false, 0);
            }

            var fieldNode = fieldNodes[0];
            ctx.Dispatcher.Post(() => ctx.Runtime.SetFocus(fieldNode));
            if (!WaitFor(ctx, () => UiApplication.CurrentHost?.Router?.TsfEnabled == true
                    ? UiApplication.CurrentHost.Router.TsfBridge?.ActiveClient is not null
                    : bridge.ActiveClient is not null, 5_000, ct))
            {
                failures.Add("session-not-started");
                return (false, 0);
            }

            log.Record("script", "host", "session", new Dictionary<string, object?>
            {
                ["active_client"] = true,
                ["capabilities"] = bridge.Capabilities.ToString(),
                // 后置核实：TSF 惰性建立在首次 StartSession——此前 input-backend 只反映未创建状态，
                // 此时记录真实 ActiveBackend/BackendReason/session_present，不据前置值冒充 TSF 证据。
                ["active_backend"] = router?.ActiveBackend.ToString() ?? "(none)",
                ["backend_reason"] = router?.BackendReason ?? "(none)",
                ["tsf_session_present"] = router?.TsfBridge?.Session is not null,
                ["tsf_enabled"] = router?.TsfEnabled == true,
            });

            // 自有 HIMC 输入模式：保存真实原状态，场景组结束后在 finally 恢复——
            // 只写本窗口上下文的 open/conversion，不触碰任何全局/profile 设置。
            // 读取也在生产 UI 线程：HIMC 操作与窗口线程上下文相关，不用脚本线程读数冒充。
            var (open0, conv0, sent0, readOk) = Query(ctx, () =>
            {
                var ok = bridge.TryGetNativeInputState(out var o, out var c, out var s);
                return (o, c, s, ok);
            }, ct);
            (bool Open, uint Conversion, uint Sentence)? savedImeState = readOk
                ? (open0, conv0, sent0)
                : null;

            log.Record("env", "host", "native-input-state", new Dictionary<string, object?>
            {
                ["readable"] = savedImeState is not null,
                ["open"] = open0,
                ["conversion"] = $"0x{conv0:X4}",
                ["sentence"] = $"0x{sent0:X4}",
                ["note"] = "微软拼音未必服从 IMM 转换位；若切换无效如实记录为路线证据，不谎报。",
            });

            try
            {
                ran = RunScenarios(log, outDir, script, ctx, hwnd, bridge, harness, bindingRead, binding2Read,
                    externalWrite, layoutModeRead, setPhase, failures, hostExited,
                    scenarioFilter, savedImeState, manualEnglish, manualWaitMs, useTsf, englishKeyboard, ct);
            }
            finally
            {
                if (savedImeState is { } saved)
                {
                    // 恢复只写本窗口自有 HIMC：窗口已销毁则 HIMC 一并消亡，属“无须恢复”而非失败；
                    // 窗口仍存活时全部经 UI 线程读写并回读确认，前台仍属本窗口时允许 Shift 兜底。
                    // --manual-english 下程序全程未写任何 IME 模式位（用户手动切换），同样无须恢复。
                    if (manualEnglish)
                    {
                        log.Record("script", "host", "native-input-state-restore-skipped",
                            new Dictionary<string, object?>
                            {
                                ["reason"] = "manual-mode",
                                ["note"] = "人工英文模式：程序未写任何 IME 模式位，模式由用户自行恢复，无须程序恢复。",
                            });
                    }
                    else if (!NativeMethods.IsWindow(hwnd) || hostExited.IsSet)
                    {
                        log.Record("script", "host", "native-input-state-restore-skipped",
                            new Dictionary<string, object?>
                            {
                                ["reason"] = "window-destroyed",
                                ["note"] = "窗口已销毁，自有 HIMC 随之消亡——无须恢复，不视作恢复失败。",
                            });
                    }
                    else if (!RestoreNativeMode(log, ctx, bridge, script, hwnd, saved, ct))
                    {
                        failures.Add("native-mode-restore-failed");
                    }
                }
            }
        }
        finally
        {
            if (restoreProfile is { } original)
            {
                try
                {
                    if (original.ProfileType == TsfProfile.TfProfileTypeInputProcessor)
                    {
                        var (hr, detail) = TsfProfile.ActivateForProcess(
                            log, "hostcheck-restore", original.Clsid, original.ProfileGuid, original.LangId);
                        log.Record("script", "tsf", "profile-restored", new Dictionary<string, object?>
                        {
                            ["hr"] = $"0x{hr:X8}",
                            ["detail"] = detail,
                        });
                    }
                    else
                    {
                        log.Record("script", "tsf", "profile-restore-skipped", new Dictionary<string, object?>
                        {
                            ["reason"] = "original active profile is not an input processor; "
                                + "FORPROCESS activation expires with this process and session default was never touched",
                            ["original_type"] = original.ProfileTypeName,
                        });
                    }
                }
                catch (Exception ex)
                {
                    log.Record("script", "tsf", "profile-restore-fault",
                        new Dictionary<string, object?> { ["exception"] = ex.GetType().Name });
                }
            }

            TsfProfile.UninitializeCom();
        }

        return (false, ran);
    }

    /// <summary>场景前置：从当前 Root 重新查找已挂载 TextField（绝不跨 Responsive 分支缓存节点），
    /// 滚轮回到顶部、聚焦并等锚点有效。返回 false 时该场景不运行（记失败）。</summary>
    /// <remarks>
    /// 前置顺序：1) 自有前台确认——短暂丢失时给用户一次有限的重新点击窗口，不做任何自动抢前台；
    /// 2) 清理上一场景可能残留的组合；3) 滚回可见顶部；4) 重新查挂载字段并聚焦；5) 等会话+锚点。
    /// </remarks>
    private static bool EnsureFieldReady(ProbeLog log, string scenario, InputScript script,
        UiHostContext ctx, WindowsImeBridge bridge, IntPtr hwnd, int fieldIndex, CancellationToken ct)
    {
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            log.Record("script", "scenario", "foreground-rewait", new Dictionary<string, object?>
            {
                ["scenario"] = scenario,
                ["note"] = "前台丢失；请重新点击本工具窗口。有限等待期间不发送任何按键。",
            });
            if (!WaitForUserForeground(hwnd, 15_000, ct))
            {
                LogFailure(log, scenario, "foreground-not-owned");
                return false;
            }
        }

        // 上一场景可能残留组合（场景内清理失败时不污染本场景前置）。TSF 模式读
        // TsfBridge 快照判据，IMM 模式仍读 ActiveClient——不能把 IMM 读数当 TSF 证据。
        var tsfPreclean = UiApplication.CurrentHost?.Router?.TsfBridge;
        var useTsfHere = UiApplication.CurrentHost?.Router?.TsfEnabled == true && tsfPreclean is not null;
        bool PrecleanComposing() => useTsfHere
            ? tsfPreclean!.Session.Owner.Snapshot.IsComposing
            : bridge.ActiveClient?.IsComposing == true;
        if (Query(ctx, () => PrecleanComposing(), ct))
        {
            script.PressVirtualKey(VkEscape, scenario + "-preclean-esc");
            WaitFor(ctx, () => !PrecleanComposing(), 5_000, ct);
        }

        // 滚轮回顶部（正 delta 沿正常 Wheel 路径；偏移由容器钳位，不依赖具体步长）。
        Query(ctx, () =>
        {
            ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, 100));
            return true;
        }, ct);
        var nodes = Query(ctx, () => ctx.Runtime.Root.FindDescendants<TextField>().ToList(), ct);
        if (fieldIndex >= nodes.Count || !nodes[fieldIndex].IsMounted)
        {
            LogFailure(log, scenario, $"field-{fieldIndex}-not-mounted");
            return false;
        }

        var node = nodes[fieldIndex];
        ctx.Dispatcher.Post(() => ctx.Runtime.SetFocus(node));
        var ready = WaitFor(ctx,
            () => UiApplication.CurrentHost?.Router?.TsfEnabled == true
                ? UiApplication.CurrentHost.Router.TsfBridge?.ActiveClient is not null
                    && UiApplication.CurrentHost.Router.TsfBridge.Session.GeometryProvider.Current is not null
                : bridge.ActiveClient is not null && bridge.LastAnchorClient is not null,
            5_000, ct);
        if (!ready)
        {
            LogFailure(log, scenario, "session-or-anchor-not-ready");
        }

        return ready;
    }

    /// <summary>在 UI 线程回读自有 HIMC 的 open/conversion；只读。</summary>
    private static (bool Open, uint Conversion, bool Ok) ReadNativeMode(
        UiHostContext ctx, WindowsImeBridge bridge, CancellationToken ct)
        => Query(ctx, () =>
        {
            var ok = bridge.TryGetNativeInputState(out var o, out var c, out _);
            return (o, c, ok);
        }, ct);

    /// <summary>切换自有 HIMC 的中/英文转换模式并回读验证；全部在生产 UI 线程经调度器执行。
    /// 微软拼音若忽略 IMM 转换位（set_ok=false 或回读未达目标），在已确认自有前台、且无活动组合的
    /// 前提下按一次物理 Shift 切换中英文并再次回读确认——不据 API 返回值猜测模式。</summary>
    private static bool SetNativeMode(ProbeLog log, UiHostContext ctx, WindowsImeBridge bridge,
        bool chinese, (bool Open, uint Conversion, uint Sentence) saved, CancellationToken ct,
        InputScript? script = null, IntPtr hwnd = default)
    {
        var conv = chinese
            ? saved.Conversion | Win32Ime.IME_CMODE_NATIVE
            : saved.Conversion & ~Win32Ime.IME_CMODE_NATIVE;
        var set = Query(ctx, () => bridge.TrySetNativeInputState(true, conv, saved.Sentence), ct);
        var (openB, convB, readBack) = ReadNativeMode(ctx, bridge, ct);
        var effective = readBack && openB
            && ((convB & Win32Ime.IME_CMODE_NATIVE) != 0) == chinese;
        var method = "imm";

        // IMM 后备：只对本进程自有前台窗口按一次 Shift（微软拼音中英文切换），回读实际位确认。
        if (!effective
            && script is not null
            && hwnd != IntPtr.Zero
            && NativeMethods.GetForegroundWindow() == hwnd
            && Query(ctx, () => bridge.ActiveClient?.IsComposing != true, ct)
            && script.PressVirtualKey(VkShift, chinese ? "ime-mode-shift-cn" : "ime-mode-shift-en"))
        {
            method = "shift-key";
            effective = WaitFor(ctx, () =>
            {
                var ok = bridge.TryGetNativeInputState(out var o, out var c, out _);
                return ok && o && ((c & Win32Ime.IME_CMODE_NATIVE) != 0) == chinese;
            }, 3_000, ct);
            (openB, convB, _) = ReadNativeMode(ctx, bridge, ct);
        }

        log.Record("script", "host", "ime-mode", new Dictionary<string, object?>
        {
            ["target"] = chinese ? "chinese" : "english",
            ["method"] = method,
            ["set_ok"] = set,
            ["readback_open"] = openB,
            ["readback_conversion"] = $"0x{convB:X4}",
            ["effective"] = effective,
            ["note"] = "无效时如实记录为路线证据；Shift 后备只在自有前台窗口、无活动组合时触发。",
        });
        return effective;
    }

    /// <summary>把自有 HIMC 恢复到进入场景组前保存的模式；全部读写经 UI 线程，结果按回读判定。
    /// IMM 写回无效且自有前台仍属本窗口时允许一次 Shift 兜底；最终仍不符如实记录恢复失败。</summary>
    private static bool RestoreNativeMode(ProbeLog log, UiHostContext ctx, WindowsImeBridge bridge,
        InputScript script, IntPtr hwnd, (bool Open, uint Conversion, uint Sentence) saved,
        CancellationToken ct)
    {
        var (openB, convB, readBack) = ReadNativeMode(ctx, bridge, ct);
        var matches = readBack
            && openB == saved.Open
            && ((convB ^ saved.Conversion) & Win32Ime.IME_CMODE_NATIVE) == 0;
        var method = "already-matches";
        var setOk = false;
        if (!matches)
        {
            method = "imm";
            setOk = Query(ctx,
                () => bridge.TrySetNativeInputState(saved.Open, saved.Conversion, saved.Sentence), ct);
            matches = WaitFor(ctx, () =>
            {
                var ok = bridge.TryGetNativeInputState(out var o, out var c, out _);
                return ok && o == saved.Open
                    && ((c ^ saved.Conversion) & Win32Ime.IME_CMODE_NATIVE) == 0;
            }, 1_500, ct);

            if (!matches
                && NativeMethods.GetForegroundWindow() == hwnd
                && Query(ctx, () => bridge.ActiveClient?.IsComposing != true, ct)
                && script.PressVirtualKey(VkShift, "ime-mode-restore-shift"))
            {
                method = "shift-key";
                matches = WaitFor(ctx, () =>
                {
                    var ok = bridge.TryGetNativeInputState(out var o, out var c, out _);
                    return ok && o == saved.Open
                        && ((c ^ saved.Conversion) & Win32Ime.IME_CMODE_NATIVE) == 0;
                }, 3_000, ct);
            }

            (openB, convB, readBack) = ReadNativeMode(ctx, bridge, ct);
        }

        log.Record("script", "host", "native-input-state-restored", new Dictionary<string, object?>
        {
            ["restored"] = matches,
            ["method"] = method,
            ["set_ok"] = setOk,
            ["readback_open"] = openB,
            ["readback_conversion"] = $"0x{convB:X4}",
            ["saved_open"] = saved.Open,
            ["saved_conversion"] = $"0x{saved.Conversion:X4}",
            ["note"] = matches ? "原自有 HIMC 模式已恢复（回读确认）。"
                : "窗口仍存活但未能恢复——如实记录，不谎称窗口已销毁。",
        });
        return matches;
    }

    /// <summary>串里是否含 CJK 统一表意字符（精确中文结果判据的一部分）。</summary>
    private static bool ContainsCjk(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '一' and <= '鿿')
            {
                return true;
            }
        }

        return false;
    }
    private static int RunScenarios(
        ProbeLog log,
        string outDir,
        InputScript script,
        UiHostContext ctx,
        IntPtr hwnd,
        WindowsImeBridge bridge,
        HarnessComponent harness,
        Func<(string Text, int Writes)> bindingRead,
        Func<(string Text, int Writes)> binding2Read,
        Action<string> externalWrite,
        Func<string> layoutModeRead,
        Action<string> setPhase,
        List<string> failures,
        ManualResetEventSlim hostExited,
        string? scenarioFilter,
        (bool Open, uint Conversion, uint Sentence)? savedImeState,
        bool manualEnglish,
        int manualWaitMs,
        bool useTsf,
        bool englishKeyboard,
        CancellationToken ct)
    {
        var compositionObserved = false;
        var scenariosRan = 0;
        // 每次实际执行的 Scenario 都经此计数——退出码与日志据实报告运行数，
        // 过滤后 0 场景不允许算通过。
        bool S(ProbeLog l, string name, List<string> f, Func<bool> run)
        {
            scenariosRan++;
            return Scenario(l, name, f, run);
        }
        bool IsComposing() => useTsf
            ? UiApplication.CurrentHost?.Router?.TsfBridge?.Session.Owner.Snapshot.IsComposing == true
            : bridge.ActiveClient?.IsComposing == true;
        // TSF 判据助手：几何有效性读 TsfBridge.Session.GeometryProvider（IMM 仍读 LastAnchorClient），
        // 撤销/发布计数读 TSF 桥自身计数——绝不把 IMM ActiveClient/counters 当 TSF 证据。
        bool GeometryValid() => useTsf
            ? UiApplication.CurrentHost?.Router?.TsfBridge?.Session.GeometryProvider.Current is not null
            : bridge.LastAnchorClient is not null;
        int GeometryRevokes() => useTsf
            ? UiApplication.CurrentHost?.Router?.TsfBridge?.GeometryClearedCount ?? 0
            : bridge.GeometryRevokeCount;
        int GeometryPublishes() => useTsf
            ? UiApplication.CurrentHost?.Router?.TsfBridge?.GeometryPublishCount ?? 0
            : bridge.GeometryPublishCount;
        string GeometryDesc() => useTsf
            ? UiApplication.CurrentHost?.Router?.TsfBridge?.Session.GeometryProvider.Current?.ToString()
                ?? "(none)"
            : bridge.LastAnchorClient?.ToString() ?? "(none)";


        // 中文场景组先把自有 HIMC 切到原生转换模式（英文组在后面单独切回）。切换无效时
        // 场景照常执行并把结果如实记为路线证据（微软拼音可能无视该位）。
        // --manual-english 下不写任何 IME 模式位：英文由用户手动切换，本前置不得再强制切中文。
        if (!manualEnglish && !useTsf && savedImeState is { } ime0)
        {
            SetNativeMode(log, ctx, bridge, true, ime0, ct, script, hwnd);
        }
        if (ShouldRun(scenarioFilter, "nihao-commit"))
        {
            compositionObserved |= S(log, "nihao-commit", failures, () =>
            {
                if (!EnsureFieldReady(log, "nihao-commit", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var framesBeforeInput = Query(ctx, () => ctx.RenderedFrameCount, ct);
                var before = bindingRead();
                if (!script.SendAsciiText("nihao", "nihao-commit")
                    || !WaitFor(ctx, () => useTsf
                        ? UiApplication.CurrentHost?.Router?.TsfBridge?.Session.Owner.Snapshot.IsComposing == true
                        : bridge.ActiveClient?.IsComposing == true, 5_000, ct))
                {
                    LogFailure(log, "nihao-commit", "no-composition-observed");
                    return false;
                }

                var diagnostic = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindingRead().Writes), ct) : null;
                var preedit = useTsf
                    ? diagnostic?.Preedit ?? string.Empty
                    : Query(ctx, () => bridge.ActiveClient!.Preedit, ct);
                var preeditBinding = bindingRead();
                log.Record("script", "scenario", "preedit", new Dictionary<string, object?>
                {
                    ["scenario"] = "nihao-commit",
                    ["preedit"] = preedit,
                    ["writes_delta"] = preeditBinding.Writes - before.Writes,
                    ["tsf_candidate"] = diagnostic?.ToLog(),
                });
                if (useTsf)
                {
                    WaitFor(ctx, () => ctx.RenderedFrameCount > framesBeforeInput, 2_000, ct);
                    Thread.Sleep(200);
                }
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s1-preedit.png"));

                if (useTsf && (diagnostic is null || !diagnostic.CandidatePresentationAvailable
                    || !diagnostic.CandidateValid || diagnostic.CandidateTexts.Length == 0
                    || preeditBinding.Writes != before.Writes))
                {
                    LogFailure(log, "nihao-commit", "tsf-candidate-or-zero-setter-preedit-failed");
                    return false;
                }
                if (!script.PressVirtualKey(VkSpace, "commit-space")
                    || !WaitFor(bindingRead, b => b.Writes != before.Writes, 8_000, ct))
                {
                    LogFailure(log, "nihao-commit", "no-commit");
                    return false;
                }

                var after = bindingRead();
                var delta = after.Writes - before.Writes;
                // 精确中文结果：第一个候选必须是“你好”且恰好提交一次。
                var expected = before.Text + "你好";
                var exact = after.Text == expected;
                log.Record("script", "scenario", "commit-result", new Dictionary<string, object?>
                {
                    ["scenario"] = "nihao-commit",
                    ["committed_text"] = after.Text,
                    ["expected_text"] = expected,
                    ["writes_delta"] = delta,
                    ["commit_exactly_once"] = delta == 1,
                    ["exact_chinese"] = exact,
                });
                return delta == 1 && exact;
            });
        }

        // ---- 场景 2：组合中 Esc 取消且不退出宿主 ----
        if (ShouldRun(scenarioFilter, "escape-cancel"))
        {
            compositionObserved |= S(log, "escape-cancel", failures, () =>
            {
                if (hostExited.IsSet)
                {
                    return false;
                }

                if (!EnsureFieldReady(log, "escape-cancel", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var before = bindingRead();
                if (!script.SendAsciiText("nihao", "esc-cancel")
                    || !WaitFor(ctx, IsComposing, 5_000, ct))
                {
                    LogFailure(log, "escape-cancel", "no-composition");
                    return false;
                }

                if (!script.PressVirtualKey(VkEscape, "escape")
                    || !WaitFor(ctx, () => !IsComposing(), 5_000, ct))
                {
                    LogFailure(log, "escape-cancel", "still-composing-or-exited");
                    return false;
                }

                var after = bindingRead();
                var ok = !hostExited.IsSet && after.Text == before.Text && after.Writes == before.Writes;
                log.Record("script", "scenario", "escape-cancel-result", new Dictionary<string, object?>
                {
                    ["host_alive"] = !hostExited.IsSet,
                    ["text_unchanged"] = after.Text == before.Text,
                    ["writes_delta"] = after.Writes - before.Writes,
                });
                return ok;
            });
        }

        // ---- 场景 3：Enter 选词路径（必须见证恰好一次真实提交；受 --scenario 过滤） ----
        if (ShouldRun(scenarioFilter, "enter-select"))
        {
            compositionObserved |= S(log, "enter-select", failures, () =>
            {
                if (!EnsureFieldReady(log, "enter-select", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var before = bindingRead();
                if (!script.SendAsciiText("nihao", "enter-select")
                    || !WaitFor(ctx, IsComposing, 5_000, ct))
                {
                    LogFailure(log, "enter-select", "no-composition");
                    return false;
                }
                var candidateReadyBeforeEnter = !useTsf || WaitFor(ctx, () =>
                {
                    var session = UiApplication.CurrentHost?.Router?.TsfBridge?.Session;
                    var snapshot = session?.UiElementSink?.LastPublished;
                    return session?.Owner.Snapshot.IsComposing == true
                        && snapshot is { Valid: true } && snapshot.Entries.Any(entry => entry.Text == "你好");
                }, 5_000, ct);
                if (!candidateReadyBeforeEnter)
                {
                    LogFailure(log, "enter-select", "candidate-not-ready");
                    return false;
                }

                if (!script.PressVirtualKey(VkReturn, "enter"))
                {
                    return false;
                }

                // Enter 必须见证一次真实提交（零提交不通过）；同时组合结束、宿主不退出。
                WaitFor(bindingRead, b => b.Writes != before.Writes, 8_000, ct);
                var after = bindingRead();
                var delta = after.Writes - before.Writes;
                var ended = WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                var committed = after.Text.Length > before.Text.Length
                    ? after.Text[before.Text.Length..] : "";
                // Preserve exact submit/no-leak checks and distinguish selecting the visible Chinese candidate
                // from Microsoft Pinyin committing the raw preedit despite a ready candidate list.
                var noNewline = !committed.Contains('\n') && !committed.Contains('\r');
                var selectedCandidate = committed == "你好";
                var rawCommitted = committed == "nihao";
                var safetyOk = delta == 1 && ended && noNewline && !hostExited.IsSet;
                log.Record("script", "scenario", "enter-select-result", new Dictionary<string, object?>
                {
                    ["writes_delta"] = delta,
                    ["commit_witnessed"] = delta == 1,
                    ["committed_chunk"] = committed,
                    ["candidate_ready_before_enter"] = candidateReadyBeforeEnter,
                    ["enter_selected_candidate"] = selectedCandidate,
                    ["enter_commits_raw_with_candidates"] = useTsf && candidateReadyBeforeEnter && rawCommitted,
                    ["enter_outcome"] = selectedCandidate ? "candidate-selected"
                        : useTsf && rawCommitted ? "Enter确认原始拼音（本机微软拼音行为）" : "other-commit",
                    ["no_newline_or_submit"] = noNewline,
                    ["composition_ended"] = ended,
                    ["host_alive"] = !hostExited.IsSet,
                    ["safety_result"] = safetyOk ? "pass" : "fail",
                });
                return safetyOk;
            });
        }

        // ---- 场景 4：连续分段组合（两段拼音各选词一次，断言真实中文结果） ----
        if (ShouldRun(scenarioFilter, "segmented"))
        {
            compositionObserved |= S(log, "segmented", failures, () =>
            {
                if (!EnsureFieldReady(log, "segmented", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var before = bindingRead();
                if (!script.SendAsciiText("nihao", "seg-1")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !script.PressVirtualKey(VkSpace, "seg-commit-1")
                    || !WaitFor(bindingRead, b => b.Writes > before.Writes, 8_000, ct))
                {
                    LogFailure(log, "segmented", "first-segment-failed");
                    return false;
                }

                var mid = bindingRead();
                var seg1 = mid.Text[before.Text.Length..];
                if (!script.SendAsciiText("shijie", "seg-2")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !script.PressVirtualKey(VkSpace, "seg-commit-2")
                    || !WaitFor(bindingRead, b => b.Writes > mid.Writes, 8_000, ct))
                {
                    LogFailure(log, "segmented", "second-segment-failed");
                    return false;
                }

                var after = bindingRead();
                var delta = after.Writes - before.Writes;
                var expected = before.Text + "你好" + "世界";
                log.Record("script", "scenario", "segmented-result", new Dictionary<string, object?>
                {
                    ["text"] = after.Text,
                    ["expected_text"] = expected,
                    ["seg1"] = seg1,
                    ["writes_delta"] = delta,
                    ["two_commits_exact"] = delta == 2 && after.Text == expected,
                });
                return delta == 2 && after.Text == expected;
            });
        }

        // ---- 场景 5：英文普通输入——默认先把自有 HIMC 切英文（IMM 失败时自有前台 Shift 兜底）；
        //      --manual-english 下改为提示用户手动切英文并按 F10 确认，程序不写任何 IME 模式位 ----
        if (ShouldRun(scenarioFilter, "ascii-plain"))
        {
            S(log, "ascii-plain", failures, () =>
            {
                if (!EnsureFieldReady(log, "ascii-plain", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                try
                {
                var switched = false;
                var confirmObserved = false;
                var modeSource = "none";
                if (manualEnglish)
                {
                    // R16：微软拼音对 IMM 转换位与注入扫描码 Shift 均不响应（set_ok=true 但回读恒为
                    // 0x0401）——可信路径是真实用户切换。提示显示在自有窗口标题栏与控制台，
                    // 用户确认键 F10 经 GetAsyncKeyState 只读检测；不写任何 IME 模式位。
                    modeSource = "manual-user";
                    var originalTitle = Query(ctx, () => UiApplication.CurrentHost!.Window.Title, ct);
                    const string promptTitle = "ImeHostCheck: 请切换到英文输入模式（按 Shift 或点击语言栏）后按 F10 继续";
                    Query(ctx, () =>
                    {
                        UiApplication.CurrentHost!.Window.Title = promptTitle;
                        return true;
                    }, ct);
                    Console.WriteLine(promptTitle);
                    log.Record("script", "scenario", "manual-english-prompt", new Dictionary<string, object?>
                    {
                        ["wait_ms"] = manualWaitMs,
                        ["confirm_key"] = "F10",
                    });
                    try
                    {
                        confirmObserved = WaitForConfirmKey(hwnd, VkF10, manualWaitMs, ct, hostExited);
                    }
                    finally
                    {
                        Query(ctx, () =>
                        {
                            UiApplication.CurrentHost!.Window.Title = originalTitle;
                            return true;
                        }, ct);
                    }

                    // 未获确认（超时/宿主退出/前台非本窗口按下）：记录失败并直接返回，
                    // 绝不向未确认状态的输入法发送 abc。
                    if (!confirmObserved)
                    {
                        log.Record("script", "scenario", "ascii-plain-result",
                            new Dictionary<string, object?>
                            {
                                ["mode_source"] = modeSource,
                                ["confirm_observed"] = false,
                                ["keys_sent"] = false,
                                ["note"] = "未在本窗口前台等到 F10 按下并松开——未发送任何按键。",
                            });
                        LogFailure(log, "ascii-plain", "manual-english-not-confirmed");
                        return false;
                    }
                }
                else if (englishKeyboard)
                {
                    // 既有已启用英文键盘布局 profile：先 GetProfile 核对 TF_IPP_FLAG_ENABLED，
                    // 再 FORPROCESS 激活并回读实际活动身份；场景末恢复原活动 profile（同一契约
                    // 与 --profile mspinyin 相同——FORPROCESS only，不改启用/默认）。
                    modeSource = "english-keyboard-profile";
                    switched = SelectEnglishKeyboardLayout(log, ctx, ct);
                    if (!switched)
                    {
                        LogFailure(log, "ascii-plain", "english-keyboard-profile-unavailable");
                        return false;
                    }
                }
                else
                {
                    modeSource = "programmatic";
                    switched = savedImeState is { } sv
                        && SetNativeMode(log, ctx, bridge, false, sv, ct, script, hwnd);
                }

                var before = bindingRead();
                if (!script.SendAsciiText("abc", "ascii-plain"))
                {
                    LogFailure(log, "ascii-plain", "send-failed");
                    return false;
                }

                var imeIntercepted = WaitFor(ctx, IsComposing, 800, ct);
                var landed = WaitFor(bindingRead,
                    b => b.Text == before.Text + "abc" && b.Writes == before.Writes + 3, 5_000, ct);
                var after = bindingRead();
                log.Record("script", "scenario", "ascii-plain-result", new Dictionary<string, object?>
                {
                    ["landed_exact"] = landed,
                    ["text_delta"] = after.Text.Length > before.Text.Length
                        ? after.Text[before.Text.Length..] : "",
                    ["writes_delta"] = after.Writes - before.Writes,
                    ["mode_source"] = modeSource,
                    ["mode_switch_effective"] = switched,
                    ["confirm_observed"] = confirmObserved,
                    ["ime_took_keys"] = imeIntercepted,
                    ["note"] = manualEnglish
                        ? "人工英文模式：判据只认真实文本写入与无组合；用户自行负责恢复中文模式。"
                        : (!switched
                            ? "自有 HIMC 模式切换无效或不可用——此为路线证据，不是通道失败证据。"
                            : "英文模式下逐字符经已提交文本通道写入。"),
                });
                if (imeIntercepted)
                {
                    script.PressVirtualKey(VkEscape, "ascii-cancel-leftover");
                    WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                }

                // 恢复中文模式供后续中文场景；恢复无效如实记录。manual 模式下程序未写模式位，
                // 只提示用户自行恢复，不做程序性切换。
                if (!manualEnglish && !englishKeyboard && savedImeState is { } sv2)
                {
                    SetNativeMode(log, ctx, bridge, true, sv2, ct, script, hwnd);
                }
                else if (manualEnglish)
                {
                    Console.WriteLine("ImeHostCheck: ascii-plain 已结束，请自行恢复中文输入模式。");
                }

                return landed && !imeIntercepted;
                }
                finally
                {
                    // 英文 profile 作用域恢复：原活动 TSF profile + 原 HKL 都回读核对；
                    // 在 finally 中执行——即使场景早退/异常也恢复，不遗留已切换 profile。
                    if (englishKeyboard)
                    {
                        var restored = RestoreEnglishInput(log, ctx, ct);
                        if (!restored)
                        {
                            LogFailure(log, "ascii-plain", "english-keyboard-restore-failed");
                        }
                    }
                }
            });
        }

        // ---- 场景 5b：数字键选词——数字不写成文本，提交恰为一次中文结果 ----
        if (ShouldRun(scenarioFilter, "digit-select"))
        {
            compositionObserved |= S(log, "digit-select", failures, () =>
            {
                if (!EnsureFieldReady(log, "digit-select", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var before = bindingRead();
                if (!script.SendAsciiText("nihao", "digit-select")
                    || !WaitFor(ctx, IsComposing, 5_000, ct))
                {
                    LogFailure(log, "digit-select", "no-composition");
                    return false;
                }

                if (!script.PressVirtualKey(VkD1, "digit-1"))
                {
                    return false;
                }

                WaitFor(bindingRead, b => b.Writes != before.Writes, 8_000, ct);
                var after = bindingRead();
                var delta = after.Writes - before.Writes;
                var ended = !IsComposing();
                var committed = after.Text.Length > before.Text.Length
                    ? after.Text[before.Text.Length..] : "";
                var isChinese = ContainsCjk(committed);
                log.Record("script", "scenario", "digit-select-result", new Dictionary<string, object?>
                {
                    ["writes_delta"] = delta,
                    ["composition_ended"] = ended,
                    ["committed_chunk"] = committed,
                    ["digit_not_written"] = !after.Text.EndsWith('1'),
                    ["committed_is_cjk"] = isChinese,
                });
                return delta == 1 && ended && !after.Text.EndsWith('1') && isChinese;
            });
        }

        // ---- 场景 5c：KEYEVENTF_UNICODE 直接字符通道（含代理对；精确文本与次数） ----
        if (ShouldRun(scenarioFilter, "unicode-direct"))
        {
            S(log, "unicode-direct", failures, () =>
            {
                if (!EnsureFieldReady(log, "unicode-direct", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var before = bindingRead();
                // 中（BMP 单码元）+ 😀（代理对两个码元）：UTF-16 边界各发一次真实消息。
                if (!script.SendUnicodeCodeUnit((ushort)'中', "unicode-zhong")
                    || !script.SendUnicodeCodeUnit((ushort)'\uD83D', "unicode-hi")
                    || !script.SendUnicodeCodeUnit((ushort)'\uDE00', "unicode-lo"))
                {
                    return false;
                }

                // 文本事件按标量投递：“中”一次、完整代理对“😀”一次 → 恰好两次 setter 写入（不是按码元计）。
                var landed = WaitFor(bindingRead,
                    b => b.Text == before.Text + "中😀" && b.Writes == before.Writes + 2, 5_000, ct);
                var after = bindingRead();
                log.Record("script", "scenario", "unicode-direct-result", new Dictionary<string, object?>
                {
                    ["landed_exact"] = landed,
                    ["text_delta"] = after.Text.Length > before.Text.Length
                        ? after.Text[before.Text.Length..] : "",
                    ["writes_delta"] = after.Writes - before.Writes,
                    ["note"] = "合成字符通道见证（不是 IME 证据）：BMP 单码元 + 代理对各自一次标量提交（setter +2）。",
                });
                return landed;
            });
        }

        // ---- 场景 5d：组合中切换焦点——本地取消、旧段绝不写入新字段 ----
        if (ShouldRun(scenarioFilter, "focus-switch-cancel"))
        {
            compositionObserved |= S(log, "focus-switch-cancel", failures, () =>
            {
                if (!EnsureFieldReady(log, "focus-switch-cancel", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var nodes = Query(ctx, () => ctx.Runtime.Root.FindDescendants<TextField>().ToList(), ct);
                if (nodes.Count < 2 || !nodes[1].IsMounted)
                {
                    LogFailure(log, "focus-switch-cancel", "field-2-not-mounted");
                    return false;
                }

                var before1 = bindingRead();
                var before2 = binding2Read();
                if (!script.SendAsciiText("nihao", "focus-switch")
                    || !WaitFor(ctx, IsComposing, 5_000, ct))
                {
                    LogFailure(log, "focus-switch-cancel", "no-composition");
                    return false;
                }

                ctx.Dispatcher.Post(() => ctx.Runtime.SetFocus(nodes[1]));
                var cancelled = WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                var after1 = bindingRead();
                var after2 = binding2Read();
                var ok = cancelled
                    && after1.Text == before1.Text && after1.Writes == before1.Writes
                    && after2.Text == before2.Text && after2.Writes == before2.Writes;
                log.Record("script", "scenario", "focus-switch-cancel", new Dictionary<string, object?>
                {
                    ["cancelled"] = cancelled,
                    ["field1_unchanged"] = after1.Text == before1.Text,
                    ["field1_writes_delta"] = after1.Writes - before1.Writes,
                    ["field2_writes_delta"] = after2.Writes - before2.Writes,
                });

                ctx.Dispatcher.Post(() => ctx.Runtime.SetFocus(nodes[0]));
                WaitFor(ctx, () => useTsf
                    ? UiApplication.CurrentHost?.Router?.TsfBridge?.ActiveClient is not null
                    : bridge.ActiveClient is not null, 5_000, ct);
                return ok;
            });
        }

        // ---- 场景 6a：组合中容器真实滚动——先小幅滚动看锚点跟随重发，再完全滚出看几何撤销 ----
        if (ShouldRun(scenarioFilter, "container-scroll"))
        {
            compositionObserved |= S(log, "container-scroll", failures, () =>
            {
                if (useTsf)
                {
                    return RunTsfContainerScroll(log, outDir, script, ctx, bridge, hwnd, bindingRead, ct);
                }

                if (!EnsureFieldReady(log, "container-scroll", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                // 前置：必须有真实活动组合 + 有效锚点，否则 offset 增长不代表候选跟随。
                if (!script.SendAsciiText("nihao", "scroll-preedit")
                    || !WaitFor(ctx, () => bridge.ActiveClient?.IsComposing == true, 5_000, ct)
                    || !WaitFor(ctx, () => bridge.LastAnchorClient is not null, 5_000, ct))
                {
                    LogFailure(log, "container-scroll", "no-composition-or-anchor");
                    return false;
                }

                var anchorBefore = bridge.LastAnchorClient;
                var publishesBefore = bridge.GeometryPublishCount;
                var scroll0 = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7a-composing.png"));

                // 小幅滚动（精确 7 逻辑像素）：caret 仍在视口内，锚点应跟随重发而不是撤销。
                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -7,
                        KeyModifiers.None, WheelDeltaMode.Pixels));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scroll0, 5_000, ct);
                var scrollSmall = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                var publishDeltaSmall = bridge.GeometryPublishCount - publishesBefore;
                var anchorAfterSmall = bridge.LastAnchorClient;
                // 给系统候选窗一次重排机会再截图（候选位置由系统更新，需要时间）。
                Thread.Sleep(400);
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7b-small-scroll.png"));

                // 完全滚出视口：几何必须撤销（裁出即无效）。
                var revokesBefore = bridge.GeometryRevokeCount;
                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -500,
                        KeyModifiers.None, WheelDeltaMode.Pixels));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scrollSmall, 5_000, ct);
                var scrollOut = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                var revoked = WaitFor(ctx,
                    () => bridge.LastAnchorClient is null
                        || bridge.GeometryRevokeCount > revokesBefore, 5_000, ct);
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7c-scrolled-out.png"));
                var stillComposing = Query(ctx, () => bridge.ActiveClient?.IsComposing == true, ct);

                // 清理：取消残留组合并滚回可见顶部，保证后续场景前置独立。
                if (stillComposing && NativeMethods.GetForegroundWindow() == hwnd)
                {
                    script.PressVirtualKey(VkEscape, "scroll-cancel-leftover");
                    WaitFor(ctx, () => bridge.ActiveClient?.IsComposing != true, 5_000, ct);
                }

                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, 100));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY == 0, 5_000, ct);

                var ok = scrollSmall > scroll0
                    && publishDeltaSmall > 0
                    && anchorAfterSmall is not null
                    && scrollOut > scrollSmall
                    && revoked;
                log.Record("script", "scenario", "container-scroll", new Dictionary<string, object?>
                {
                    ["scroll_before"] = scroll0,
                    ["scroll_small"] = scrollSmall,
                    ["scroll_out"] = scrollOut,
                    ["anchor_before"] = anchorBefore?.ToString() ?? "(none)",
                    ["anchor_after_small"] = anchorAfterSmall?.ToString() ?? "(none)",
                    ["publish_delta_small"] = publishDeltaSmall,
                    ["revoked_when_scrolled_out"] = revoked,
                    ["still_composing_after"] = stillComposing,
                    ["note"] = "小幅滚动见证锚点跟随重发；滚出视口见证几何撤销。候选窗像素级跟随需人工核对 s7 截图。",
                });
                return ok;
            });
        }

        // ---- 场景 6b：断点重排——先有真实组合+有效锚点，跨断点重排后验证旧会话取消/锚点撤销，
        //      再重新查询挂载字段恢复会话与几何；恢复原窗口宽度供后续场景 ----
        if (ShouldRun(scenarioFilter, "breakpoint-reflow"))
        {
            compositionObserved |= S(log, "breakpoint-reflow", failures, () =>
            {
                if (!EnsureFieldReady(log, "breakpoint-reflow", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "reflow-preedit")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "breakpoint-reflow", "no-composition-or-anchor");
                    return false;
                }

                var modeBefore = layoutModeRead();
                var revokesBefore = GeometryRevokes();
                var sizeBefore = Query(ctx, () => UiApplication.CurrentHost!.Window.ClientSize, ct);
                // 跨过 460 逻辑像素断点改窗口宽度；布局真实重排。
                var targetWidth = modeBefore == "narrow-vstack" ? 700 : 380;
                Query(ctx, () =>
                {
                    var w = UiApplication.CurrentHost!.Window;
                    w.ClientSize = new global::OpenTK.Mathematics.Vector2i(targetWidth, w.ClientSize.Y);
                    return true;
                }, ct);
                var switched = WaitFor(ctx, () => layoutModeRead() != modeBefore, 5_000, ct);
                var modeAfter = layoutModeRead();
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s8-breakpoint.png"));

                // 断点替换/重锚节点时，旧组合必须被取消或锚点被撤销——不接受重排后仍挂着旧几何。
                var oldSettled = WaitFor(ctx, () =>
                    !IsComposing()
                    || !GeometryValid()
                    || GeometryRevokes() > revokesBefore, 5_000, ct);
                if (Query(ctx, () => IsComposing(), ct)
                    && NativeMethods.GetForegroundWindow() == hwnd)
                {
                    script.PressVirtualKey(VkEscape, "reflow-cancel-leftover");
                    WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                }

                // 恢复原宽度并把字段滚动回可见区、重新聚焦——后续场景不得依赖本场景的节点/滚动残留。
                Query(ctx, () =>
                {
                    var w = UiApplication.CurrentHost!.Window;
                    w.ClientSize = sizeBefore;
                    return true;
                }, ct);
                WaitFor(ctx, () => layoutModeRead() == modeBefore, 5_000, ct);
                var reestablished = EnsureFieldReady(
                    log, "breakpoint-reflow", script, ctx, bridge, hwnd, 0, ct);

                log.Record("script", "scenario", "breakpoint-reflow", new Dictionary<string, object?>
                {
                    ["mode_before"] = modeBefore,
                    ["mode_after"] = modeAfter,
                    ["layout_switched"] = switched,
                    ["old_session_settled"] = oldSettled,
                    ["revokes_delta"] = GeometryRevokes() - revokesBefore,
                    ["reestablished_after_reflow"] = reestablished,
                    ["note"] = "断点重排证据：模式真实切换 + 旧会话取消/锚点撤销 + 新挂载字段会话重建。",
                });
                return switched && oldSettled && reestablished;
            });
        }

        // ---- 场景 6：组合中移动/缩放自有窗口，锚点重新发布 ----
        if (ShouldRun(scenarioFilter, "anchor-follow"))
        {
            compositionObserved |= S(log, "anchor-follow", failures, () =>
            {
                if (!EnsureFieldReady(log, "anchor-follow", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "anchor-follow")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "anchor-follow", "no-composition-or-anchor");
                    return false;
                }

                // 先让系统候选窗按当前锚点完成一次布局，再抓“移动前”基准图与身份/写入见证。
                Thread.Sleep(400);
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s6-composing-before.png"));
                var publishesBefore = GeometryPublishes();
                var anchorBefore = GeometryDesc();
                var bindBefore = bindingRead();
                var stateBefore = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindBefore.Writes), ct) : null;
                var rectBefore = Query(ctx, () =>
                {
                    var w = UiApplication.CurrentHost!.Window;
                    NativeMethods.GetWindowRect(hwnd, out var r);
                    return $"{r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}";
                }, ct);
                // 用 Win32 SetWindowPos 真实移动窗口（不改大小/z-order，不抢激活）。
                var rect = Query(ctx, () =>
                {
                    NativeMethods.GetWindowRect(hwnd, out var r);
                    return r;
                }, ct);
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left + 60, rect.Top + 60, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                var republished = WaitFor(ctx, () => GeometryPublishes() > publishesBefore, 5_000, ct);
                var afterMovePublishes = GeometryPublishes() - publishesBefore;
                // 移动后留时间让系统候选窗完成跟随重排再截图；身份/写入在重发后核对。
                Thread.Sleep(400);
                var bindAfter = bindingRead();
                var anchorAfterMove = GeometryDesc();
                var stateAfter = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s6-composing-moved.png"));

                var sameIdentity = useTsf
                    ? IdentityMatches(stateBefore, stateAfter)
                    : Query(ctx, () => bridge.ActiveClient?.IsComposing == true, ct);
                var zeroWrites = bindAfter.Writes == bindBefore.Writes && bindAfter.Text == bindBefore.Text;
                // TSF 判据：同一组合移动中保留（同一 CompositionId/session/UIElement id+generation/preedit）、
                // setter 零写、几何真实重发；随后空格唯一提交“你好”（不 Esc 清理，验证组合存活可用）。
                var commitOk = true;
                if (useTsf)
                {
                    var spaceSent = NativeMethods.GetForegroundWindow() == hwnd
                        && script.PressVirtualKey(VkSpace, "anchor-commit-space");
                    var committed = spaceSent && WaitFor(bindingRead,
                        b => b.Writes > bindAfter.Writes, 8_000, ct);
                    var afterCommit = bindingRead();
                    commitOk = committed && afterCommit.Text == bindBefore.Text + "你好"
                        && afterCommit.Writes - bindBefore.Writes == 1;
                    log.Record("script", "scenario", "anchor-follow-commit", new Dictionary<string, object?>
                    {
                        ["committed_text"] = afterCommit.Text,
                        ["expected_text"] = bindBefore.Text + "你好",
                        ["writes_delta"] = afterCommit.Writes - bindBefore.Writes,
                        ["commit_exactly_once"] = commitOk,
                    });
                }
                else if (NativeMethods.GetForegroundWindow() == hwnd)
                {
                    script.PressVirtualKey(VkEscape, "anchor-cancel");
                    WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                }

                log.Record("script", "scenario", "anchor-follow", new Dictionary<string, object?>
                {
                    ["rect_before"] = rectBefore,
                    ["anchor_before"] = anchorBefore,
                    ["anchor_after"] = anchorAfterMove,
                    ["publish_count_delta"] = afterMovePublishes,
                    ["republished"] = republished,
                    ["identity_same_composition"] = sameIdentity,
                    ["writes_unchanged_during_move"] = zeroWrites,
                    ["commit_exactly_once"] = commitOk,
                    ["state_before"] = stateBefore?.ToLog(),
                    ["state_moved"] = stateAfter?.ToLog(),
                    ["note"] = "窗口移动后平台锚点缓存已清空并真正重发；候选窗可见性需人工核对前后截图。",
                });
                return republished && sameIdentity && zeroWrites && commitOk;
            });
        }

        // ---- 场景 8a2：同 Responsive 分支真实 resize（物理 +80px，仍 >460 逻辑 wide-hstack，不过断点）——
        //      同 Session/CompositionId/预编辑/0写，surface 新客户区正确，候选随最终几何、唯一提交。
        //      与 breakpoint-reflow（跨分支允许取消）不同：同分支 resize 不得丢组合。 ----
        if (ShouldRun(scenarioFilter, "resize-samebranch"))
        {
            compositionObserved |= S(log, "resize-samebranch", failures, () =>
            {
                if (!EnsureFieldReady(log, "resize-samebranch", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }
                if (!script.SendAsciiText("nihao", "resize-preedit")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "resize-samebranch", "no-composition-or-anchor");
                    return false;
                }

                var modeBefore = layoutModeRead();
                var revokesBefore = GeometryRevokes();
                var publishesBefore = GeometryPublishes();
                var bindBefore = bindingRead();
                var stateBefore = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindBefore.Writes), ct) : null;
                var sizeBefore = Query(ctx, () => UiApplication.CurrentHost!.Window.ClientSize, ct);

                // 同分支 resize：ClientSize 为物理像素（150% 下 480 逻辑=720 物理）。本窗口初始
                // 480 逻辑 wide-hstack；±80 物理像素仍在 >460 逻辑同分支内（DPI 变化如实记边界）。
                if (modeBefore != "wide-hstack")
                {
                    LogFailure(log, "resize-samebranch", $"not-in-wide-branch mode={modeBefore}");
                    return false;
                }
                var targetWidth = sizeBefore.X + 80;
                setPhase("resize-enter");
                Query(ctx, () =>
                {
                    var w = UiApplication.CurrentHost!.Window;
                    w.ClientSize = new global::OpenTK.Mathematics.Vector2i(targetWidth, w.ClientSize.Y);
                    return true;
                }, ct);
                // 等新客户区生效并至少一帧渲染。
                var frame = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
                var resized = WaitFor(ctx, () =>
                    UiApplication.CurrentHost?.Window.ClientSize.X == targetWidth
                    && ctx.RenderedFrameCount > frame, 5_000, ct);
                setPhase("resized");
                var modeDuring = layoutModeRead();
                var sizeDuring = Query(ctx, () => UiApplication.CurrentHost!.Window.ClientSize, ct);
                var bindDuring = bindingRead();
                var stateDuring = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindDuring.Writes), ct) : null;
                var stillComposing = Query(ctx, () => IsComposing(), ct);
                var anchorDuring = GeometryDesc();
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s8r-resized.png"));

                // 提交：唯一 `你好` 写入；同 Session/CompositionId/预编辑须在 resize 后仍保持。
                script.PressVirtualKey(VkSpace, "resize-commit");
                var committed = WaitFor(ctx, () => !IsComposing(), 5_000, ct)
                    && WaitFor(bindingRead,
                        b => b.Text == bindBefore.Text + "你好" && b.Writes == bindBefore.Writes + 1, 5_000, ct);
                var bindAfter = bindingRead();
                var stateAfter = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;

                var sameIdentity = useTsf
                    ? IdentityMatches(stateBefore, stateDuring)
                    : stillComposing;
                var samePreedit = useTsf
                    ? stateBefore?.Preedit == stateDuring?.Preedit
                    : stillComposing;
                var zeroWrites = bindDuring.Writes == bindBefore.Writes;

                log.Record("script", "scenario", "resize-samebranch", new Dictionary<string, object?>
                {
                    ["mode_before"] = modeBefore,
                    ["mode_during"] = modeDuring,
                    ["size_before"] = $"{sizeBefore.X}x{sizeBefore.Y}",
                    ["size_target"] = $"{targetWidth}x{sizeBefore.Y}",
                    ["size_during"] = $"{sizeDuring.X}x{sizeDuring.Y}",
                    ["client_size_applied"] = resized,
                    ["still_composing"] = stillComposing,
                    ["identity_same_composition"] = sameIdentity,
                    ["same_preedit"] = samePreedit,
                    ["zero_writes_during_resize"] = zeroWrites,
                    ["writes_before"] = bindBefore.Writes,
                    ["writes_during"] = bindDuring.Writes,
                    ["writes_after"] = bindAfter.Writes,
                    ["anchor_during"] = anchorDuring,
                    ["publish_delta"] = GeometryPublishes() - publishesBefore,
                    ["revoke_delta"] = GeometryRevokes() - revokesBefore,
                    ["commit_exactly_once"] = committed,
                    ["state_before"] = stateBefore?.ToLog(),
                    ["state_during"] = stateDuring?.ToLog(),
                    ["state_after"] = stateAfter?.ToLog(),
                    ["note"] = "同 Responsive 分支真实 resize（物理宽度 +80，仍 >460 逻辑 wide-hstack 不过断点）："
                        + "同 Session/CompositionId/预编辑/0写须保持，新客户区 surface 生效，"
                        + "候选随最终几何并唯一提交。与跨分支 reflow 允许取消判据相反。",
                });
                return resized && stillComposing && sameIdentity && samePreedit && zeroWrites && committed
                    && modeDuring == "wide-hstack";
            });
        }

        // ---- 场景 8b：组合中最小化/恢复——起始必须有有效锚点；最小化见证撤销，恢复后重建会话与几何 ----
        if (ShouldRun(scenarioFilter, "minimize-restore"))
        {
            compositionObserved |= S(log, "minimize-restore", failures, () =>
            {
                if (!EnsureFieldReady(log, "minimize-restore", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "minimize-preedit")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "minimize-restore", "no-composition-or-anchor");
                    return false;
                }

                var revokesBefore = GeometryRevokes();
                var anchorBefore = GeometryDesc();
                var bindBefore = bindingRead();
                var stateBefore = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindBefore.Writes), ct) : null;
                setPhase("minimize-enter");
                Query(ctx, () =>
                {
                    var w = UiApplication.CurrentHost!.Window;
                    w.WindowState = global::OpenTK.Windowing.Common.WindowState.Minimized;
                    return true;
                }, ct);
                setPhase("minimized");
                var revokedWhileMin = WaitFor(ctx,
                    () => !GeometryValid() || GeometryRevokes() > revokesBefore, 5_000, ct);
                var anchorWhileMin = GeometryDesc();
                var bindWhileMin = bindingRead();
                var stateWhileMin = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindWhileMin.Writes), ct) : null;

                setPhase("minimize-restore-enter");
                Query(ctx, () =>
                {
                    var w = UiApplication.CurrentHost!.Window;
                    w.WindowState = global::OpenTK.Windowing.Common.WindowState.Normal;
                    return true;
                }, ct);
                setPhase("minimize-restore-return");
                // 恢复后重新查挂载字段、聚焦并等有效几何——恢复可能丢前台，EnsureFieldReady
                // 给用户有限重点击窗口，不做自动抢前台。
                var reestablished = EnsureFieldReady(
                    log, "minimize-restore", script, ctx, bridge, hwnd, 0, ct);
                var anchorRestored = GeometryDesc();
                var bindAfter = bindingRead();
                var stateAfter = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;
                var stillComposing = Query(ctx, () => IsComposing(), ct);
                if (stillComposing && NativeMethods.GetForegroundWindow() == hwnd)
                {
                    script.PressVirtualKey(VkEscape, "minimize-cancel-leftover");
                    WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                }

                // 恢复后同宿主真实输入：新 epoch 重建会话后对 nihao 组合按 Space，
                // 断言文本尾部恰追加“你好”且 setter 恰好 +1（唯一提交，新 CompositionId）。
                var bindRestore = bindingRead();
                var restoreCommitOk = reestablished
                    && script.SendAsciiText("nihao", "restore-preedit")
                    && WaitFor(ctx, () => IsComposing(), 5_000, ct)
                    && Query(ctx, () =>
                    {
                        ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s8a2-restored-composing.png"));
                        return true;
                    }, ct)
                    && Query(ctx, () =>
                    {
                        script.PressVirtualKey(VkSpace, "restore-commit");
                        return true;
                    }, ct)
                    && WaitFor(bindingRead,
                        b => b.Text.EndsWith("你好", StringComparison.Ordinal)
                            && b.Writes == bindRestore.Writes + 1, 5_000, ct);
                var bindCommit = bindingRead();
                var stateCommit = useTsf
                    ? Query(ctx, () => ReadTsfCandidateState(bindCommit.Writes), ct) : null;

                log.Record("script", "scenario", "minimize-restore", new Dictionary<string, object?>
                {
                    ["anchor_before"] = anchorBefore,
                    ["revoked_while_min"] = revokedWhileMin,
                    ["anchor_while_min"] = anchorWhileMin,
                    ["writes_before"] = bindBefore.Writes,
                    ["writes_while_min"] = bindWhileMin.Writes,
                    ["writes_after"] = bindAfter.Writes,
                    ["text_while_min"] = bindWhileMin.Text,
                    ["text_after"] = bindAfter.Text,
                    ["zero_writes_while_hidden"] = bindWhileMin.Writes == bindBefore.Writes,
                    ["reestablished"] = reestablished,
                    ["anchor_restored"] = anchorRestored,
                    ["still_composing_after"] = stillComposing,
                    ["state_before"] = stateBefore?.ToLog(),
                    ["state_while_min"] = stateWhileMin?.ToLog(),
                    ["state_commit"] = stateCommit?.ToLog(),
                    ["restore_commit_text"] = bindCommit.Text,
                    ["restore_commit_writes"] = bindCommit.Writes,
                    ["restore_commit_ok"] = restoreCommitOk,
                    ["new_epoch_session_id"] = stateCommit?.CompositionSessionId,
                    ["new_epoch_composition_id"] = stateCommit?.CompositionId,
                    ["new_epoch_generation"] = stateCommit?.CurrentGeneration,
                    ["note"] = "起始必须有真实组合+有效锚点；最小化见证几何撤销，恢复后重建会话与几何。",
                });
                return revokedWhileMin && reestablished && anchorRestored != "(none)"
                    && bindWhileMin.Writes == bindBefore.Writes // 失活取消不得把预编辑写进绑定
                    && restoreCommitOk;                        // 新 epoch 唯一提交“你好”
            });
        }

        // ---- 场景 8c：整个窗口隐藏/恢复（IsVisible）——窗口级隐藏走失活路径，组合被取消合法；
        //      与 TI-06“视图级隐藏保留组合、仅撤销几何”的语义不同，此处不要求隐藏中组合仍在 ----
        if (ShouldRun(scenarioFilter, "hide-restore"))
        {
            compositionObserved |= S(log, "hide-restore", failures, () =>
            {
                if (!EnsureFieldReady(log, "hide-restore", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "hide-preedit")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "hide-restore", "no-composition-or-anchor");
                    return false;
                }

                var revokesBefore = GeometryRevokes();
                var bindBefore = bindingRead();
                var stateBefore = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindBefore.Writes), ct) : null;
                setPhase("hide-enter");
                Query(ctx, () =>
                {
                    UiApplication.CurrentHost!.Window.IsVisible = false;
                    return true;
                }, ct);
                setPhase("hidden");
                var revokedWhileHidden = WaitFor(ctx,
                    () => !GeometryValid() || GeometryRevokes() > revokesBefore, 5_000, ct);
                var composingWhileHidden = Query(ctx, () => IsComposing(), ct);
                var bindWhileHidden = bindingRead();
                var stateWhileHidden = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindWhileHidden.Writes), ct) : null;

                setPhase("hide-restore-enter");
                Query(ctx, () =>
                {
                    UiApplication.CurrentHost!.Window.IsVisible = true;
                    return true;
                }, ct);
                setPhase("hide-restore-return");
                var reestablished = EnsureFieldReady(
                    log, "hide-restore", script, ctx, bridge, hwnd, 0, ct);
                var anchorRestored = GeometryDesc();
                var bindAfter = bindingRead();
                var stateAfter = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;
                var stillComposing = Query(ctx, () => IsComposing(), ct);
                if (stillComposing && NativeMethods.GetForegroundWindow() == hwnd)
                {
                    script.PressVirtualKey(VkEscape, "hide-cancel-leftover");
                    WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                }

                // 恢复后同宿主真实输入：新 epoch 重建会话后对 nihao 组合按 Space，
                // 断言文本尾部恰追加“你好”且 setter 恰好 +1（唯一提交，新 CompositionId）。
                var bindRestore = bindingRead();
                var restoreCommitOk = reestablished
                    && script.SendAsciiText("nihao", "restore-preedit")
                    && WaitFor(ctx, () => IsComposing(), 5_000, ct)
                    && Query(ctx, () =>
                    {
                        ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s8c-restored-composing.png"));
                        return true;
                    }, ct)
                    && Query(ctx, () =>
                    {
                        script.PressVirtualKey(VkSpace, "restore-commit");
                        return true;
                    }, ct)
                    && WaitFor(bindingRead,
                        b => b.Text.EndsWith("你好", StringComparison.Ordinal)
                            && b.Writes == bindRestore.Writes + 1, 5_000, ct);
                var bindCommit = bindingRead();
                var stateCommit = useTsf
                    ? Query(ctx, () => ReadTsfCandidateState(bindCommit.Writes), ct) : null;
                log.Record("script", "scenario", "hide-restore", new Dictionary<string, object?>
                {
                    ["revoked_while_hidden"] = revokedWhileHidden,
                    ["composing_while_hidden"] = composingWhileHidden,
                    ["writes_before"] = bindBefore.Writes,
                    ["writes_while_hidden"] = bindWhileHidden.Writes,
                    ["writes_after"] = bindAfter.Writes,
                    ["text_while_hidden"] = bindWhileHidden.Text,
                    ["text_after"] = bindAfter.Text,
                    ["zero_writes_while_hidden"] = bindWhileHidden.Writes == bindBefore.Writes,
                    ["reestablished"] = reestablished,
                    ["anchor_restored"] = anchorRestored,
                    ["still_composing_after"] = stillComposing,
                    ["state_before"] = stateBefore?.ToLog(),
                    ["state_while_hidden"] = stateWhileHidden?.ToLog(),
                    ["state_commit"] = stateCommit?.ToLog(),
                    ["restore_commit_text"] = bindCommit.Text,
                    ["restore_commit_writes"] = bindCommit.Writes,
                    ["restore_commit_ok"] = restoreCommitOk,
                    ["new_epoch_session_id"] = stateCommit?.CompositionSessionId,
                    ["new_epoch_composition_id"] = stateCommit?.CompositionId,
                    ["new_epoch_generation"] = stateCommit?.CurrentGeneration,
                    ["note"] = "窗口级 IsVisible=false 属失活路径：几何撤销、组合被取消均合法；"
                        + "恢复后须重建会话且几何重新有效。与视图级隐藏保留组合的 TI-06 语义不同。",
                });
                return revokedWhileHidden && reestablished && anchorRestored != "(none)"
                    && bindWhileHidden.Writes == bindBefore.Writes // 失活取消不得把预编辑写进绑定
                    && restoreCommitOk;                          // 新 epoch 唯一提交“你好”
            });
        }

        // ---- 场景 8d：窗口级真实失活——自有顶层辅助 HWND 取得前台（不是抢回本工具；
        //      SetForegroundWindow 目标是自有窗口，前台离开使 FocusedChanged(false) 触发
        //      NotifyWindowActivation(false) → 失活契约先 cancel、几何撤销、零写） ----
        if (ShouldRun(scenarioFilter, "window-deactivate"))
        {
            compositionObserved |= S(log, "window-deactivate", failures, () =>
            {
                if (!EnsureFieldReady(log, "window-deactivate", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "deactivate-preedit")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "window-deactivate", "no-composition-or-anchor");
                    return false;
                }

                var revokesBefore = GeometryRevokes();
                var anchorBefore = GeometryDesc();
                var bindBefore = bindingRead();
                var stateBefore = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindBefore.Writes), ct) : null;

                // 自有顶层辅助窗口——CreateWindowEx/ShowWindow/SetActiveWindow 全部在 UI 线程
                // 执行（前台/活动状态按拥有者线程输入队列归属；宿主 HWND 属 UI 线程，辅助窗
                // 必须同线程创建激活才是合法同队列前台转移）。绝不 SetForegroundWindow/
                // AttachThreadInput/模拟鼠标；自然激活未取得前台如实记前置不成立。
                setPhase("deactivate-aux-activate");
                var auxInfo = Query(ctx, () => AuxActivate(hwnd), ct);
                log.Record("script", "scenario", "window-deactivate-aux", auxInfo);
                setPhase("deactivated");
                var auxOk = auxInfo["created"] is true;
                if (!auxOk)
                {
                    LogFailure(log, "window-deactivate", "aux-window-unavailable");
                    return false;
                }
                try
                {
                    // 失活判定：不以 GetForegroundWindow 单一值定可达——观察 GLFW FocusedChanged(false)
                    // 驱动的 Runtime.IsWindowActive、TsfHostSession.IsFocused、GetFocus/GetActiveWindow
                    // 与前台根四路，任一路真实离开宿主即证真实失活到达；全部未动才记前置不成立。
                    var leftFg = WaitFor(log, "foreground-left", () =>
                        NativeMethods.GetForegroundWindow() != hwnd ? (object?)true : null, 5_000, ct) is not null;
                    var lostActive = WaitFor(ctx, () => !ctx.Runtime.IsWindowActive, 3_000, ct);
                    var lostTsfFocus = WaitFor(ctx, () =>
                        {
                            var s = UiApplication.CurrentHost?.Router?.TsfBridge?.Session;
                            // session 已销毁=真实失活已到（focus 无从谈起），不误称"TSF 仍 focused"。
                            return s is null || s.IsFocused == false;
                        }, 3_000, ct);
                    var post = Query(ctx, () => new Dictionary<string, object?>
                    {
                        ["get_focus"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
                        ["get_active"] = $"0x{NativeMethods.GetActiveWindow().ToInt64():X}",
                        ["foreground"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}",
                        ["window_is_focused_glfw"] = UiApplication.CurrentHost?.Window.IsFocused,
                        ["runtime_window_active"] = ctx.Runtime.IsWindowActive,
                        ["tsf_session_present"] = UiApplication.CurrentHost?.Router?.TsfBridge?.Session is not null,
                        ["tsf_session_focused"] = UiApplication.CurrentHost?.Router?.TsfBridge?.Session?.IsFocused,
                    }, ct);
                    var reallyLeft = leftFg || lostActive || lostTsfFocus
                        || post["window_is_focused_glfw"] is false
                        || post["get_active"] is string ga && ga.Equals($"0x{_auxHwnd.ToInt64():X}", StringComparison.OrdinalIgnoreCase)
                        || post["get_focus"] is string gf && gf.Equals($"0x{_auxHwnd.ToInt64():X}", StringComparison.OrdinalIgnoreCase);
                    if (!reallyLeft)
                    {
                        log.Record("script", "scenario", "window-deactivate", new Dictionary<string, object?>
                        {
                            ["anchor_before"] = anchorBefore,
                            ["foreground_left"] = leftFg,
                            ["post_activation"] = post,
                            ["note"] = "UI 线程 ShowWindow/SetActiveWindow 后前台/活动/焦点/GLFW IsFocused/"
                                + "Runtime.IsWindowActive/TSF IsFocused 六路均未离宿主——自然失活不可达，"
                                + "如实记前置不成立（不 SetForegroundWindow 不伪造 focus 回调）。",
                        });
                        return false;
                    }

                    var deactivated = WaitFor(ctx,
                        () => !GeometryValid() || GeometryRevokes() > revokesBefore, 5_000, ct);
                    var composingDeactivated = Query(ctx, () => IsComposing(), ct);
                    var bindWhileAway = bindingRead();
                    var stateWhileAway = useTsf
                        ? Query(ctx, () => ReadTsfCandidateState(bindWhileAway.Writes), ct) : null;

                    // 关闭辅助窗让前台/焦点自然回落给自有宿主窗，等真实 FocusedChanged(true)。
                    setPhase("deactivate-aux-destroy");
                    Query(ctx, () => { AuxDestroy(); return true; }, ct);
                    var back = WaitFor(ctx, () => ctx.Runtime.IsWindowActive, 10_000, ct);
                    var fgBack = WaitFor(log, "foreground-returned", () =>
                        NativeMethods.GetForegroundWindow() == hwnd ? (object?)true : null, 10_000, ct);
                    var reestablished = (back || fgBack is not null)
                        && EnsureFieldReady(log, "window-deactivate", script, ctx, bridge, hwnd, 0, ct);
                    var bindAfter = bindingRead();
                    var stateAfter = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;
                    var stillComposing = Query(ctx, () => IsComposing(), ct);
                    if (stillComposing && NativeMethods.GetForegroundWindow() == hwnd)
                    {
                        script.PressVirtualKey(VkEscape, "deactivate-cancel-leftover");
                        WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                    }

                    // 恢复后同宿主真实输入：新 epoch 重建会话后对 nihao 组合按 Space，
                    // 断言文本尾部恰追加“你好”且 setter 恰好 +1（唯一提交，新 CompositionId）。
                    var bindRestore = bindingRead();
                    var restoreCommitOk = reestablished
                        && script.SendAsciiText("nihao", "restore-preedit")
                        && WaitFor(ctx, () => IsComposing(), 5_000, ct)
                        && Query(ctx, () =>
                        {
                            ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s8d-restored-composing.png"));
                            return true;
                        }, ct)
                        && Query(ctx, () =>
                        {
                            script.PressVirtualKey(VkSpace, "restore-commit");
                            return true;
                        }, ct)
                        && WaitFor(bindingRead,
                            b => b.Text.EndsWith("你好", StringComparison.Ordinal)
                                && b.Writes == bindRestore.Writes + 1, 5_000, ct);
                    var bindCommit = bindingRead();
                    var stateCommit = useTsf
                        ? Query(ctx, () => ReadTsfCandidateState(bindCommit.Writes), ct) : null;
                    log.Record("script", "scenario", "window-deactivate", new Dictionary<string, object?>
                    {
                        ["anchor_before"] = anchorBefore,
                        ["foreground_left"] = leftFg,
                        ["window_active_lost"] = lostActive,
                        ["tsf_focus_lost"] = lostTsfFocus,
                        ["post_activation"] = post,
                        ["geometry_revoked"] = deactivated,
                        ["composing_while_away"] = composingDeactivated,
                        ["writes_before"] = bindBefore.Writes,
                        ["writes_while_away"] = bindWhileAway.Writes,
                        ["writes_after"] = bindAfter.Writes,
                        ["text_while_away"] = bindWhileAway.Text,
                        ["text_after"] = bindAfter.Text,
                        ["zero_writes_while_away"] = bindWhileAway.Writes == bindBefore.Writes,
                        ["window_active_returned"] = back,
                        ["foreground_returned"] = fgBack is not null,
                        ["reestablished"] = reestablished,
                        ["still_composing_after"] = stillComposing,
                        ["state_before"] = stateBefore?.ToLog(),
                        ["state_while_away"] = stateWhileAway?.ToLog(),
                        ["state_commit"] = stateCommit?.ToLog(),
                        ["restore_commit_text"] = bindCommit.Text,
                        ["restore_commit_writes"] = bindCommit.Writes,
                        ["restore_commit_ok"] = restoreCommitOk,
                        ["new_epoch_session_id"] = stateCommit?.CompositionSessionId,
                        ["new_epoch_composition_id"] = stateCommit?.CompositionId,
                        ["new_epoch_generation"] = stateCommit?.CurrentGeneration,
                        ["note"] = "真实窗口级失活：UI 线程自有顶层辅助窗 CreateWindowEx+ShowWindow+"
                            + "SetActiveWindow 自然激活（不 SetForegroundWindow 不 AttachThreadInput"
                            + " 不模拟鼠标不伪造 focus 回调）；失活观察 GLFW FocusedChanged→IsWindowActive/"
                            + "IsFocused/GetFocus/GetActiveWindow/foreground 四路任一真实离开即证据；"
                            + "销毁辅助窗等 IsWindowActive/前台真实回落。",
                    });
                    return reallyLeft && deactivated && reestablished
                        && bindWhileAway.Writes == bindBefore.Writes // 失活取消不得把预编辑写进绑定
                        && restoreCommitOk;                        // 新 epoch 唯一提交“你好”
                }
                finally
                {
                    Query(ctx, () => { AuxDestroy(); return true; }, ct);
                }
            });
        }

        // ---- 场景 8e：TextField 横向文本滚动——真实组合中内部 ScrollX>0 时锚点仍有效、
        //      提交恰一次。非 IMM：TSF 几何快照的 scroll 即 TextFieldState.ScrollX，是锚点同源。 ----
        if (ShouldRun(scenarioFilter, "horizontal-scroll"))
        {
            compositionObserved |= S(log, "horizontal-scroll", failures, () =>
            {
                if (!EnsureFieldReady(log, "horizontal-scroll", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                // 先用多轮 nihao+Space 提交把文本填到超出字段宽度（360），使 TextField
                // 内部 ScrollX>0；每轮核对唯一提交且经 TSF 几何快照读取 ScrollX。
                var writesBeforeFill = bindingRead().Writes;
                var fills = 0;
                var scrolled = false;
                for (var i = 0; i < 32 && !scrolled; i++)
                {
                    if (!script.SendAsciiText("nihao", $"hfill-preedit-{i}")
                        || !WaitFor(ctx, IsComposing, 5_000, ct))
                    {
                        LogFailure(log, "horizontal-scroll", $"fill-compose-failed-{i}");
                        return false;
                    }
                    script.PressVirtualKey(VkSpace, $"hfill-commit-{i}");
                    if (!WaitFor(ctx, () => !IsComposing(), 5_000, ct))
                    {
                        LogFailure(log, "horizontal-scroll", $"fill-commit-failed-{i}");
                        return false;
                    }
                    fills++;
                    // 等待一帧让 ScrollX 收敛，再读几何快照的 scroll 值。
                    var f = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
                    WaitFor(ctx, () => ctx.RenderedFrameCount > f, 5_000, ct);
                    scrolled = Query(ctx, () =>
                        (UiApplication.CurrentHost?.Router?.TsfBridge?.Session?.GeometryProvider.Current?.ScrollX ?? 0) > 0.5, ct);
                }
                var bindFilled = bindingRead();
                var expectedWrites = writesBeforeFill + fills;
                if (bindFilled.Writes != expectedWrites)
                {
                    LogFailure(log, "horizontal-scroll",
                        $"fill-writes-mismatch expected={expectedWrites} actual={bindFilled.Writes}");
                    return false;
                }

                // 横向滚动下的新组合：锚点须有效且几何快照 ScrollX>0。
                var revokesBefore = GeometryRevokes();
                if (!script.SendAsciiText("nihao", "hscroll-preedit")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !WaitFor(ctx, GeometryValid, 5_000, ct))
                {
                    LogFailure(log, "horizontal-scroll", "no-composition-or-anchor");
                    return false;
                }
                var anchorComposing = GeometryDesc();
                var scrollComposing = Query(ctx, () =>
                    UiApplication.CurrentHost?.Router?.TsfBridge?.Session?.GeometryProvider.Current?.ScrollX ?? -1, ct);
                var bindComposing = bindingRead();
                var stateComposing = useTsf
                    ? Query(ctx, () => ReadTsfCandidateState(bindComposing.Writes), ct) : null;
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s-hscroll-composing.png"));

                script.PressVirtualKey(VkSpace, "hscroll-commit");
                var bindBase = bindFilled;
                var committed = WaitFor(ctx, () => !IsComposing(), 5_000, ct)
                    && WaitFor(bindingRead,
                        b => b.Text == bindBase.Text + "你好" && b.Writes == bindBase.Writes + 1,
                        5_000, ct);
                var bindAfter = bindingRead();
                var stateAfter = useTsf ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;

                log.Record("script", "scenario", "horizontal-scroll", new Dictionary<string, object?>
                {
                    ["fills_committed"] = fills,
                    ["fill_writes_exact"] = bindFilled.Writes == expectedWrites,
                    ["scrolled_detected"] = scrolled,
                    ["anchor_composing"] = anchorComposing,
                    ["scroll_x_composing"] = scrollComposing,
                    ["revokes_during_compose"] = GeometryRevokes() - revokesBefore,
                    ["writes_before_commit"] = bindComposing.Writes,
                    ["writes_after"] = bindAfter.Writes,
                    ["text_after"] = bindAfter.Text,
                    ["commit_exactly_once"] = committed,
                    ["state_composing"] = stateComposing?.ToLog(),
                    ["state_after"] = stateAfter?.ToLog(),
                    ["note"] = "真实横向滚动：多轮 nihao/Space 把字段填超宽（TextField 内部 ScrollX>0 经 TSF "
                        + "几何快照同源读取），新组合下锚点仍有效且唯一提交。非窗口 resize。",
                });
                return scrolled && committed;
            });
        }

        // ---- 场景 8f：组合中真实禁用/卸载 TextField——Component 状态驱动 .Disabled(true)
        //      或条件卸载（真实节点移除），组合必须取消、几何撤销、零写；恢复后字段仍可用。 ----
        if (ShouldRun(scenarioFilter, "disabled-unmount"))
        {
            compositionObserved |= S(log, "disabled-unmount", failures, () =>
            {
                if (!EnsureFieldReady(log, "disabled-unmount", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }
                if (harness.FieldDisabled is null || harness.FieldMounted is null)
                {
                    LogFailure(log, "disabled-unmount", "harness-states-not-built");
                    return false;
                }

                var cancelledDisabled = RunTogglePhase(
                    "disable", () => Query(ctx, () => { harness.FieldDisabled!.Value = true; return true; }, ct),
                    () => Query(ctx, () => { harness.FieldDisabled!.Value = false; return true; }, ct));
                var cancelledUnmount = RunTogglePhase(
                    "unmount", () => Query(ctx, () => { harness.FieldMounted!.Value = false; return true; }, ct),
                    () => Query(ctx, () => { harness.FieldMounted!.Value = true; return true; }, ct));

                log.Record("script", "scenario", "disabled-unmount", new Dictionary<string, object?>
                {
                    ["disable_phase"] = cancelledDisabled,
                    ["unmount_phase"] = cancelledUnmount,
                    ["note"] = "组合中经真实 State.Value 切换 .Disabled(true)/卸载（真实节点路径）；"
                        + "两阶段都须先 cancel、几何撤销、零写，恢复后 EnsureFieldReady 重建会话。",
                });
                return cancelledDisabled && cancelledUnmount;

                bool RunTogglePhase(string label, Func<bool> apply, Func<bool> revert)
                {
                    var ready = EnsureFieldReady(log, $"disabled-unmount/{label}", script, ctx, bridge, hwnd, 0, ct);
                    if (!ready) return false;
                    if (!script.SendAsciiText("nihao", $"{label}-preedit")
                        || !WaitFor(ctx, IsComposing, 5_000, ct)
                        || !WaitFor(ctx, GeometryValid, 5_000, ct))
                    {
                        LogFailure(log, "disabled-unmount", $"{label}-no-composition");
                        return false;
                    }
                    var revokesBefore = GeometryRevokes();
                    var bindBefore = bindingRead();
                    var stateBefore = useTsf
                        ? Query(ctx, () => ReadTsfCandidateState(bindBefore.Writes), ct) : null;

                    setPhase($"{label}-apply-enter");
                    apply();
                    setPhase($"{label}-apply-return");
                    var frame = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
                    WaitFor(ctx, () => ctx.RenderedFrameCount > frame, 5_000, ct);

                    var ended = WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                    var revoked = WaitFor(ctx,
                        () => !GeometryValid() || GeometryRevokes() > revokesBefore, 5_000, ct);
                    var bindDuring = bindingRead();
                    var stateDuring = useTsf
                        ? Query(ctx, () => ReadTsfCandidateState(bindDuring.Writes), ct) : null;

                    setPhase($"{label}-revert-enter");
                    revert();
                    setPhase($"{label}-revert-return");
                    var frame2 = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
                    WaitFor(ctx, () => ctx.RenderedFrameCount > frame2, 5_000, ct);
                    var readyBack = EnsureFieldReady(log, $"disabled-unmount/{label}-restore", script, ctx, bridge, hwnd, 0, ct);
                    var bindAfter = bindingRead();
                    var stateAfter = useTsf
                        ? Query(ctx, () => ReadTsfCandidateState(bindAfter.Writes), ct) : null;

                    var zeroWrites = bindDuring.Writes == bindBefore.Writes
                        && bindAfter.Writes == bindBefore.Writes;
                    log.Record("script", "scenario", $"disabled-unmount-{label}", new Dictionary<string, object?>
                    {
                        ["composition_ended"] = ended,
                        ["geometry_revoked"] = revoked,
                        ["zero_writes"] = zeroWrites,
                        ["writes_before"] = bindBefore.Writes,
                        ["writes_during"] = bindDuring.Writes,
                        ["writes_after"] = bindAfter.Writes,
                        ["text_during"] = bindDuring.Text,
                        ["text_after"] = bindAfter.Text,
                        ["reestablished"] = readyBack,
                        ["state_before"] = stateBefore?.ToLog(),
                        ["state_during"] = stateDuring?.ToLog(),
                        ["state_restored"] = stateAfter?.ToLog(),
                    });
                    return ended && revoked && zeroWrites && readyBack;
                }
            });
        }


        // ---- 场景 7：组合中外部绑定写入取消组合（不写 setter，走真实外部更新路径） ----
        if (ShouldRun(scenarioFilter, "external-update-cancel"))
        {
            compositionObserved |= S(log, "external-update-cancel", failures, () =>
            {
                if (!EnsureFieldReady(log, "external-update-cancel", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "external-update")
                    || !WaitFor(ctx, IsComposing, 5_000, ct))
                {
                    LogFailure(log, "external-update-cancel", "no-composition");
                    return false;
                }

                var before = bindingRead();
                // 直接改后备值（绕过 Binding setter），然后请求重绘让宿主在下一个安全阶段观察外部变化。
                externalWrite(before.Text + "|EXT");
                ctx.Dispatcher.Post(() => ctx.Runtime.InvalidateRender());

                var cancelled = WaitFor(ctx, () => !IsComposing(), 5_000, ct);
                var after = bindingRead();
                var ok = cancelled && after.Writes == before.Writes;
                log.Record("script", "scenario", "external-update-cancel", new Dictionary<string, object?>
                {
                    ["cancelled"] = cancelled,
                    ["external_writes_via_setter"] = after.Writes - before.Writes,
                    ["text_len"] = after.Text.Length,
                });
                return ok;
            });
        }

        // ---- 场景 8：撤销/重做——场景自行提交一段中文建立历史，再 Ctrl+Z/Y 验证 ----
        if (ShouldRun(scenarioFilter, "undo-redo"))
        {
            compositionObserved |= S(log, "undo-redo", failures, () =>
            {
                if (!EnsureFieldReady(log, "undo-redo", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                // 自建立可撤销历史：真实组合 + Space 提交一次（不依赖此前场景残留文本）。
                var before = bindingRead();
                if (!script.SendAsciiText("nihao", "undo-setup")
                    || !WaitFor(ctx, IsComposing, 5_000, ct)
                    || !script.PressVirtualKey(VkSpace, "undo-setup-commit")
                    || !WaitFor(bindingRead, b => b.Writes > before.Writes, 8_000, ct))
                {
                    LogFailure(log, "undo-redo", "setup-commit-failed");
                    return false;
                }

                var committed = bindingRead();
                if (!SendChord(hwnd, VkControl, VkZ))
                {
                    LogFailure(log, "undo-redo", "send-failed-or-foreground-lost");
                    return false;
                }

                var undone = WaitFor(bindingRead, b => b.Writes > committed.Writes, 5_000, ct);
                if (!undone)
                {
                    LogFailure(log, "undo-redo", "undo-not-applied");
                    return false;
                }

                var afterUndo = bindingRead();
                if (!SendChord(hwnd, VkControl, VkY)
                    || !WaitFor(bindingRead, b => b.Writes > afterUndo.Writes, 5_000, ct))
                {
                    LogFailure(log, "undo-redo", "redo-not-applied");
                    return false;
                }

                var afterRedo = bindingRead();
                var undoReverted = afterUndo.Text == before.Text;
                var ok = undoReverted && afterRedo.Text == committed.Text;
                log.Record("script", "scenario", "undo-redo", new Dictionary<string, object?>
                {
                    ["text_before_len"] = before.Text.Length,
                    ["text_committed_len"] = committed.Text.Length,
                    ["text_after_undo_len"] = afterUndo.Text.Length,
                    ["text_after_redo_len"] = afterRedo.Text.Length,
                    ["undo_reverted_commit"] = undoReverted,
                    ["redo_restored"] = ok,
                });
                return ok;
            });
        }

        // ---- 场景 9：组合进行中关闭自有窗口——会话结束 + 子类化恢复 + 原生取消 ----
        // 必须先实际见证 IsComposing=true 再关窗，并确认窗口真正销毁/宿主退出；
        // 否则“投递了 WM_CLOSE”不算证据（覆盖的是干净关闭路径）。
        if (ShouldRun(scenarioFilter, "close-window"))
        {
            compositionObserved |= S(log, "close-window", failures, () =>
            {
                if (!EnsureFieldReady(log, "close-window", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                var composingBeforeClose = script.SendAsciiText("nihao", "close-during-composition")
                    && WaitFor(ctx, IsComposing, 5_000, ct);
                if (!composingBeforeClose)
                {
                    LogFailure(log, "close-window", "no-composition-before-close");
                    return false;
                }

                var posted = NativeMethods.PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                // 确认窗口真正关闭（宿主退出或 HWND 失效），不能投递成功就当通过。
                var closed = false;
                var deadline = Environment.TickCount64 + 8_000;
                while (Environment.TickCount64 < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    if (hostExited.IsSet || !NativeMethods.IsWindow(hwnd))
                    {
                        closed = true;
                        break;
                    }

                    Thread.Sleep(50);
                }

                log.Record("script", "scenario", "close-posted", new Dictionary<string, object?>
                {
                    ["posted"] = posted,
                    ["composing_at_close"] = composingBeforeClose,
                    ["window_closed"] = closed,
                    ["note"] = "关窗后子类化恢复/回调异常计数由收尾 finish 日志核对。",
                });
                return posted && closed;
            });
        }

        // ---- 诊断场景：NI_CLOSECANDIDATE/NI_OPENCANDIDATE 对真实微软拼音候选窗的关闭-重开（R16）。
        // 只在显式 --scenario candidate-visibility-probe 时运行，不进默认矩阵；结果不是验收证据。
        if (scenarioFilter is not null && ShouldRun(scenarioFilter, "candidate-visibility-probe"))
        {
            compositionObserved |= S(log, "candidate-visibility-probe", failures, () =>
            {
                if (!EnsureFieldReady(log, "candidate-visibility-probe", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "candprobe-preedit")
                    || !WaitFor(ctx, () => bridge.ActiveClient?.IsComposing == true, 5_000, ct)
                    || !WaitFor(ctx, () => bridge.LastAnchorClient is not null, 5_000, ct))
                {
                    LogFailure(log, "candidate-visibility-probe", "no-composition-or-anchor");
                    return false;
                }

                var bindBefore = bindingRead();
                Thread.Sleep(600); // 给系统候选窗显示留时间
                var before = SnapshotComposition(ctx, bridge, hwnd, ct);
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s9a-candidate-before.png"));

                // UI 线程自有 HIMC：NI_CLOSECANDIDATE 请求关闭候选列表（不取消组合）。
                var closeRet = NotifyIme(ctx, hwnd, NativeMethods.NI_CLOSECANDIDATE, ct);
                Thread.Sleep(500);
                var afterClose = SnapshotComposition(ctx, bridge, hwnd, ct);
                var bindAfterClose = bindingRead();
                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s9b-candidate-closed.png"));

                // 组合仍在才请求重开；组合已消失则跳过并如实记录。
                var compositionLostAfterClose = !afterClose.Composing;
                var reopenCalled = false;
                var reopenOk = false;
                var afterReopen = afterClose;
                var bindAfterReopen = bindAfterClose;
                if (!compositionLostAfterClose)
                {
                    var rr = NotifyIme(ctx, hwnd, NativeMethods.NI_OPENCANDIDATE, ct);
                    reopenCalled = rr.Called;
                    reopenOk = rr.Ok;
                    Thread.Sleep(500);
                    afterReopen = SnapshotComposition(ctx, bridge, hwnd, ct);
                    bindAfterReopen = bindingRead();
                    ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s9c-candidate-reopen.png"));
                }

                // 清理：取消残留组合（仅自有前台时发 Esc）。
                if (NativeMethods.GetForegroundWindow() == hwnd
                    && Query(ctx, () => bridge.ActiveClient?.IsComposing == true, ct))
                {
                    script.PressVirtualKey(VkEscape, "candprobe-cancel");
                    WaitFor(ctx, () => bridge.ActiveClient?.IsComposing != true, 5_000, ct);
                }

                log.Record("script", "scenario", "candidate-probe-result", new Dictionary<string, object?>
                {
                    ["close_notify_called"] = closeRet.Called,
                    ["close_notify_return"] = closeRet.Ok,
                    ["composition_lost_after_close"] = compositionLostAfterClose,
                    ["reopen_notify_called"] = reopenCalled,
                    ["reopen_notify_return"] = reopenOk,
                    ["compstr_before"] = before.CompStr ?? "(none)",
                    ["compstr_after_close"] = afterClose.CompStr ?? "(none)",
                    ["compstr_after_reopen"] = afterReopen.CompStr ?? "(none)",
                    ["preedit_before"] = before.Preedit ?? "(none)",
                    ["preedit_after_close"] = afterClose.Preedit ?? "(none)",
                    ["preedit_after_reopen"] = afterReopen.Preedit ?? "(none)",
                    ["composing_before"] = before.Composing,
                    ["composing_after_close"] = afterClose.Composing,
                    ["composing_after_reopen"] = afterReopen.Composing,
                    ["writes_delta_close"] = bindAfterClose.Writes - bindBefore.Writes,
                    ["writes_delta_reopen"] = bindAfterReopen.Writes - bindAfterClose.Writes,
                    ["text_unchanged"] = bindAfterReopen.Text == bindBefore.Text,
                    ["note"] = "诊断性实验：候选窗可见性变化须由 Codex 核对 s9a/s9b/s9c 截图；"
                        + "API 返回值不代表可见性，本结果不是 R16 修复验收。",
                });
                return true; // 探针按序执行完毕即完成；候选是否消失由截图证据判定。
            });
        }

        // ---- 诊断场景：隐藏 Win32 caret（CreateCaret/SetCaretPos/DestroyCaret，绝不 ShowCaret）
        // 对照微软拼音候选是否实际读取线程 caret 位置（R16 假设验证）。只在显式 --scenario 运行。
        if (scenarioFilter is not null && ShouldRun(scenarioFilter, "native-caret-probe"))
        {
            compositionObserved |= S(log, "native-caret-probe", failures, () =>
            {
                var caretCreated = false;
                try
                {
                    if (!EnsureFieldReady(log, "native-caret-probe", script, ctx, bridge, hwnd, 0, ct))
                    {
                        return false;
                    }

                    // 组合先行：没有 native caret 时建立真实组合 + 有效锚点 + 截图 before。
                    var bindBefore = bindingRead();
                    if (!script.SendAsciiText("nihao", "caretprobe-preedit")
                        || !WaitFor(ctx, () => bridge.ActiveClient?.IsComposing == true, 5_000, ct)
                        || !WaitFor(ctx, () => bridge.LastAnchorClient is not null, 5_000, ct))
                    {
                        LogFailure(log, "native-caret-probe", "no-composition-or-anchor");
                        return false;
                    }

                    Thread.Sleep(600); // 系统候选窗显示留时间
                    var anchorBefore = bridge.LastAnchorClient;
                    var snapBefore = SnapshotComposition(ctx, bridge, hwnd, ct);
                    ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s10a-caret-before.png"));

                    // 创建守卫（UI 线程原子执行，详见 TryCreateHiddenCaret）：
                    // guiOk + 无线程 caret + 自有焦点 + 自有前台 全部满足才创建。
                    var create = TryCreateHiddenCaret(ctx, hwnd, bridge, ct);

                    log.Record("script", "scenario", "caret-create", new Dictionary<string, object?>
                    {
                        ["focus_is_own"] = create.Focus == hwnd,
                        ["foreground_is_own"] = create.Foreground == hwnd,
                        ["gui_info_ok"] = create.GuiOk,
                        ["existing_caret"] = $"0x{create.ExistingCaret.ToInt64():X}",
                        ["existing_caret_blinking"] = create.CaretBlinking,
                        ["created"] = create.Created,
                        ["set_pos_ok"] = create.SetPos,
                        ["get_caret_pos"] = create.GetPosOk ? $"({create.Pos.X},{create.Pos.Y})" : "(fail)",
                        ["anchor_before"] = anchorBefore?.ToString() ?? "(none)",
                    });
                    if (!create.Created)
                    {
                        LogFailure(log, "native-caret-probe",
                            !create.GuiOk ? "guithreadinfo-query-failed"
                                : create.ExistingCaret != IntPtr.Zero ? "existing-caret-skip"
                                : create.Focus != hwnd || create.Foreground != hwnd
                                    ? "focus-or-foreground-not-own"
                                    : "createcaret-failed");
                        return false;
                    }

                    caretCreated = true;
                    // 等待/截图/SetCaretPos 前一律复核自有前台；失焦即停止（finally 清理）。
                    if (!WaitWithOwnForeground(hwnd, 600, ct))
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-after-create");
                        return false;
                    }
                    var snapAfterCreate = SnapshotComposition(ctx, bridge, hwnd, ct);
                    ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s10b-caret-after-create.png"));

                    // 对照1：小滚动 7px，只更新桥锚点，不同步 native caret → 截图看候选跟哪边。
                    var scrollBefore = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                    Query(ctx, () =>
                    {
                        ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -7,
                            KeyModifiers.None, WheelDeltaMode.Pixels));
                        return true;
                    }, ct);
                    WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scrollBefore
                        && bridge.LastAnchorClient is not null, 5_000, ct);
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-after-scroll");
                        return false;
                    }

                    var anchorAfterScroll = bridge.LastAnchorClient;
                    var caretPosStale = Query(ctx, () =>
                    {
                        var ok = NativeMethods.GetCaretPos(out var p);
                        return (ok, p);
                    }, ct);
                    if (!WaitWithOwnForeground(hwnd, 500, ct))
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-before-stale-shot");
                        return false;
                    }

                    ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s10c-caret-stale.png"));

                    // 对照2：UI 线程 SetCaretPos 到新锚点 → 截图看候选是否跟随 caret。
                    // 前台仍属本窗口才允许修改 caret（失活队列的 caret 不再动）。
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-before-sync");
                        return false;
                    }

                    var sync = Query(ctx, () =>
                    {
                        var set = bridge.LastAnchorClient is { } a2
                            && NativeMethods.SetCaretPos(a2.X, a2.Y);
                        var ok = NativeMethods.GetCaretPos(out var p);
                        return (set, ok, p);
                    }, ct);
                    if (!WaitWithOwnForeground(hwnd, 500, ct))
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-before-synced-shot");
                        return false;
                    }
                    var snapAfterSync = SnapshotComposition(ctx, bridge, hwnd, ct);
                    var bindAfter = bindingRead();
                    ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s10d-caret-synced.png"));

                    // 对照3：可达性事件——用 NotifyWinEvent 报告刚发生的真实 SetCaretPos
                    // （OBJID_CARET/CHILDID_SELF，自有 caret 真实位置；非伪造 IME 消息）。
                    // void API，无返回值可作成功依据；候选是否响应须核对 s10e 截图。
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-before-notify");
                        return false;
                    }

                    var notified = Query(ctx, () =>
                    {
                        NativeMethods.NotifyWinEvent(
                            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, hwnd,
                            NativeMethods.OBJID_CARET, NativeMethods.CHILDID_SELF);
                        var ok = NativeMethods.GetCaretPos(out var p);
                        return (Notified: true, ok, p);
                    }, ct);
                    if (!WaitWithOwnForeground(hwnd, 500, ct))
                    {
                        LogFailure(log, "native-caret-probe", "foreground-lost-before-notified-shot");
                        return false;
                    }

                    var snapAfterNotify = SnapshotComposition(ctx, bridge, hwnd, ct);
                    ScreenCapture.CaptureWindowRegion(
                        hwnd, Path.Combine(outDir, "s10e-caret-notified.png"));

                    log.Record("script", "scenario", "caret-probe-result", new Dictionary<string, object?>
                    {
                        ["anchor_before"] = anchorBefore?.ToString() ?? "(none)",
                        ["anchor_after_scroll"] = anchorAfterScroll?.ToString() ?? "(none)",
                        ["caret_pos_stale"] = caretPosStale.ok
                            ? $"({caretPosStale.p.X},{caretPosStale.p.Y})" : "(fail)",
                        ["caret_sync_set_ok"] = sync.set,
                        ["caret_pos_synced"] = sync.ok ? $"({sync.p.X},{sync.p.Y})" : "(fail)",
                        ["caret_notified"] = notified.Notified,
                        ["caret_pos_notified"] = notified.ok
                            ? $"({notified.p.X},{notified.p.Y})" : "(fail)",
                        ["compstr_before"] = snapBefore.CompStr ?? "(none)",
                        ["compstr_after_create"] = snapAfterCreate.CompStr ?? "(none)",
                        ["compstr_after_sync"] = snapAfterSync.CompStr ?? "(none)",
                        ["compstr_after_notify"] = snapAfterNotify.CompStr ?? "(none)",
                        ["composing_end"] = snapAfterNotify.Composing,
                        ["writes_delta"] = bindAfter.Writes - bindBefore.Writes,
                        ["text_unchanged"] = bindAfter.Text == bindBefore.Text,
                        ["note"] = "诊断性对照：候选是否跟随须 Codex 核对 s10a..s10e 截图；"
                            + "API 返回值不代表候选移动，本结果不是 R16 修复验收。",
                    });
                    return true; // 诊断流程按序执行完毕；候选跟随结论由截图证据判定。
                }
                finally
                {
                    CleanupCaretProbe(log, "native-caret-probe", ctx, script, bridge,
                        hwnd, caretCreated, hostExited);
                }
            });
        }

        // ---- 诊断场景：caret 在组合开始“之前”已存在（对照：微软拼音是否在组合
        // 开始时读取既有线程 caret 而非响应后续更新）。只在显式 --scenario 运行。
        if (scenarioFilter is not null && ShouldRun(scenarioFilter, "native-caret-early-probe"))
        {
            compositionObserved |= S(log, "native-caret-early-probe", failures, () =>
            {
                var caretCreated = false;
                try
                {
                    if (!EnsureFieldReady(log, "native-caret-early-probe", script, ctx, bridge, hwnd, 0, ct))
                    {
                        return false;
                    }

                    var bindBefore = bindingRead();

                    // 组合开始前：同一套创建守卫 + Set 到当前锚点 + 真实位置通知。
                    var create = TryCreateHiddenCaret(ctx, hwnd, bridge, ct);
                    log.Record("script", "scenario", "caret-early-create", new Dictionary<string, object?>
                    {
                        ["focus_is_own"] = create.Focus == hwnd,
                        ["foreground_is_own"] = create.Foreground == hwnd,
                        ["gui_info_ok"] = create.GuiOk,
                        ["existing_caret"] = $"0x{create.ExistingCaret.ToInt64():X}",
                        ["created"] = create.Created,
                        ["set_pos_ok"] = create.SetPos,
                        ["get_caret_pos"] = create.GetPosOk ? $"({create.Pos.X},{create.Pos.Y})" : "(fail)",
                    });
                    if (!create.Created)
                    {
                        LogFailure(log, "native-caret-early-probe",
                            !create.GuiOk ? "guithreadinfo-query-failed"
                                : create.ExistingCaret != IntPtr.Zero ? "existing-caret-skip"
                                : create.Focus != hwnd || create.Foreground != hwnd
                                    ? "focus-or-foreground-not-own"
                                    : "createcaret-failed");
                        return false;
                    }

                    caretCreated = true;
                    Query(ctx, () =>
                    {
                        NativeMethods.NotifyWinEvent(
                            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, hwnd,
                            NativeMethods.OBJID_CARET, NativeMethods.CHILDID_SELF);
                        return true;
                    }, ct);

                    // 真实 nihao 组合；候选若在组合开始时读取既有 caret 应已就位。
                    if (!script.SendAsciiText("nihao", "caretearly-preedit")
                        || !WaitFor(ctx, () => bridge.ActiveClient?.IsComposing == true, 5_000, ct)
                        || !WaitFor(ctx, () => bridge.LastAnchorClient is not null, 5_000, ct))
                    {
                        LogFailure(log, "native-caret-early-probe", "no-composition-or-anchor");
                        return false;
                    }

                    if (!WaitWithOwnForeground(hwnd, 600, ct))
                    {
                        LogFailure(log, "native-caret-early-probe", "foreground-lost-before-shot");
                        return false;
                    }

                    var anchorBefore = bridge.LastAnchorClient;
                    var snapBefore = SnapshotComposition(ctx, bridge, hwnd, ct);
                    ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s11a-caret-early.png"));

                    // 小滚动 → 新锚点 → SetCaretPos + 真实位置通知 → 截图对照。
                    var scrollBefore = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                    Query(ctx, () =>
                    {
                        ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -7,
                            KeyModifiers.None, WheelDeltaMode.Pixels));
                        return true;
                    }, ct);
                    WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scrollBefore
                        && bridge.LastAnchorClient is not null, 5_000, ct);
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        LogFailure(log, "native-caret-early-probe", "foreground-lost-after-scroll");
                        return false;
                    }

                    var sync = Query(ctx, () =>
                    {
                        var set = bridge.LastAnchorClient is { } a2
                            && NativeMethods.SetCaretPos(a2.X, a2.Y);
                        NativeMethods.NotifyWinEvent(
                            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, hwnd,
                            NativeMethods.OBJID_CARET, NativeMethods.CHILDID_SELF);
                        var ok = NativeMethods.GetCaretPos(out var p);
                        return (set, ok, p);
                    }, ct);
                    if (!WaitWithOwnForeground(hwnd, 500, ct))
                    {
                        LogFailure(log, "native-caret-early-probe", "foreground-lost-before-synced-shot");
                        return false;
                    }

                    var anchorAfterScroll = bridge.LastAnchorClient;
                    var snapAfter = SnapshotComposition(ctx, bridge, hwnd, ct);
                    var bindAfter = bindingRead();
                    ScreenCapture.CaptureWindowRegion(
                        hwnd, Path.Combine(outDir, "s11b-caret-early-synced.png"));

                    log.Record("script", "scenario", "caret-early-result", new Dictionary<string, object?>
                    {
                        ["anchor_before"] = anchorBefore?.ToString() ?? "(none)",
                        ["anchor_after_scroll"] = anchorAfterScroll?.ToString() ?? "(none)",
                        ["caret_sync_set_ok"] = sync.set,
                        ["caret_pos_synced"] = sync.ok ? $"({sync.p.X},{sync.p.Y})" : "(fail)",
                        ["compstr_before"] = snapBefore.CompStr ?? "(none)",
                        ["compstr_after_sync"] = snapAfter.CompStr ?? "(none)",
                        ["composing_end"] = snapAfter.Composing,
                        ["writes_delta"] = bindAfter.Writes - bindBefore.Writes,
                        ["text_unchanged"] = bindAfter.Text == bindBefore.Text,
                        ["note"] = "诊断性对照：候选是否按既有 caret 定位须 Codex 核对 "
                            + "s11a/s11b 截图；本结果不是 R16 修复验收。",
                    });
                    return true;
                }
                finally
                {
                    CleanupCaretProbe(log, "native-caret-early-probe", ctx, script, bridge,
                        hwnd, caretCreated, hostExited);
                }
            });
        }

        // ---- 诊断场景：小滚动后追加一次真实按键（nihao→nihaoa）——判定“组合内容变化”
        // 是否驱动已开候选窗移到滚动后的新锚点（R16 路线分析 E1）。
        // 只在显式 --scenario scroll-edit-diagnostic 时运行，不进默认矩阵；结果不是验收证据。
        if (scenarioFilter is not null && ShouldRun(scenarioFilter, "scroll-edit-diagnostic"))
        {
            compositionObserved |= S(log, "scroll-edit-diagnostic", failures, () =>
            {
                if (!EnsureFieldReady(log, "scroll-edit-diagnostic", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "scrolldiag-preedit")
                    || !WaitFor(ctx, () => bridge.ActiveClient?.IsComposing == true, 5_000, ct)
                    || !WaitFor(ctx, () => bridge.LastAnchorClient is not null, 5_000, ct))
                {
                    LogFailure(log, "scroll-edit-diagnostic", "no-composition-or-anchor");
                    return false;
                }

                var bindBefore = bindingRead();
                var anchorBefore = bridge.LastAnchorClient;
                var publishesBefore = bridge.GeometryPublishCount;
                var scroll0 = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                var compBefore = SnapshotComposition(ctx, bridge, hwnd, ct);
                if (!WaitWithOwnForeground(hwnd, 400, ct))
                {
                    LogFailure(log, "scroll-edit-diagnostic", "foreground-lost-before-initial-shot");
                    return false;
                }

                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s12a-initial.png"));

                // 小滚动 7 逻辑像素：锚点应跟随重发；候选是否移动由截图判定。
                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -7,
                        KeyModifiers.None, WheelDeltaMode.Pixels));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scroll0, 5_000, ct);
                var scrollSmall = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                var anchorAfterScroll = bridge.LastAnchorClient;
                var publishDeltaScroll = bridge.GeometryPublishCount - publishesBefore;
                Thread.Sleep(400); // 给系统候选窗一次重排机会再截图
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    LogFailure(log, "scroll-edit-diagnostic", "foreground-lost-after-scroll");
                    return false;
                }

                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s12b-scroll.png"));
                var compAtScroll = SnapshotComposition(ctx, bridge, hwnd, ct);

                // 追加真实按键 'a'：nihao→nihaoa。唯一变量是组合内容变化——若候选因此
                // 移到新锚点，说明缺口在“重排触发点”而非锚点送达。按键仍走自有前台守护。
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    LogFailure(log, "scroll-edit-diagnostic", "foreground-lost-before-edit");
                    return false;
                }

                if (!script.SendAsciiText("a", "scrolldiag-append"))
                {
                    LogFailure(log, "scroll-edit-diagnostic", "append-key-rejected");
                    return false;
                }

                // 等组合串真实变化（在脚本线程轮询快照；CompStr/Preedit 任一变化即算）。
                var compAfter = compAtScroll;
                var compChanged = false;
                var editDeadline = Environment.TickCount64 + 5_000;
                while (Environment.TickCount64 < editDeadline)
                {
                    compAfter = SnapshotComposition(ctx, bridge, hwnd, ct);
                    if (compAfter.CompStr != compAtScroll.CompStr
                        || compAfter.Preedit != compAtScroll.Preedit)
                    {
                        compChanged = true;
                        break;
                    }

                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(40);
                }

                Thread.Sleep(400); // 组合更新后给候选窗重排时间再截图
                var anchorAfterEdit = bridge.LastAnchorClient;
                var bindAfter = bindingRead();
                if (NativeMethods.GetForegroundWindow() == hwnd)
                {
                    ScreenCapture.CaptureWindowRegion(
                        hwnd, Path.Combine(outDir, "s12c-after-edit.png"));
                }

                // 清理：取消残留组合并滚回顶部（仅自有前台时发 Esc）。
                if (NativeMethods.GetForegroundWindow() == hwnd
                    && Query(ctx, () => bridge.ActiveClient?.IsComposing == true, ct))
                {
                    script.PressVirtualKey(VkEscape, "scrolldiag-cancel");
                    WaitFor(ctx, () => bridge.ActiveClient?.IsComposing != true, 5_000, ct);
                }

                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, 100));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY == 0, 5_000, ct);

                var writesDelta = bindAfter.Writes - bindBefore.Writes;
                log.Record("script", "scenario", "scroll-edit-diagnostic", new Dictionary<string, object?>
                {
                    ["scroll_before"] = scroll0,
                    ["scroll_small"] = scrollSmall,
                    ["anchor_before"] = anchorBefore?.ToString() ?? "(none)",
                    ["anchor_after_scroll"] = anchorAfterScroll?.ToString() ?? "(none)",
                    ["anchor_after_edit"] = anchorAfterEdit?.ToString() ?? "(none)",
                    ["publish_delta_scroll"] = publishDeltaScroll,
                    ["compstr_before"] = compBefore.CompStr ?? "(none)",
                    ["compstr_at_scroll"] = compAtScroll.CompStr ?? "(none)",
                    ["compstr_after_edit"] = compAfter.CompStr ?? "(none)",
                    ["preedit_before"] = compBefore.Preedit ?? "(none)",
                    ["preedit_after_edit"] = compAfter.Preedit ?? "(none)",
                    ["comp_changed_after_edit"] = compChanged,
                    ["writes_delta"] = writesDelta,
                    ["text_unchanged"] = bindAfter.Text == bindBefore.Text,
                    ["note"] = "E1 诊断：追加内容是否驱动候选窗移到新锚点须 Codex 核对 "
                        + "s12a/s12b/s12c；诊断脚本通过不等于 R16 修复，原生查询 trace 见同日志 ime-request 行。",
                });
                // 诊断判据：滚动真实发生且锚点重发 + 组合串真实变化 + 未产生绑定提交。
                return scrollSmall > scroll0
                    && anchorAfterScroll is not null
                    && compChanged
                    && writesDelta == 0;
            });
        }

        // ---- 诊断场景：IMC_SETCANDIDATEPOS（官方 WM_IME_CONTROL 候选定位入口，R16 最后隔离项）。
        // 只发给本进程默认 IME 窗口：ImmGetDefaultIMEWnd 取回后必须以
        // GetWindowThreadProcessId 验证与本窗口同线程/同进程且 IsWindow；失配即记录并停止，
        // 不枚举也不操作任何其他候选/IME 窗口。返回值（0=成功）只作诊断记录；
        // 候选是否移动须 Codex 核对 s13 截图。仅发 CFS_CANDIDATEPOS——真实排除区在工具层
        // 不可读（桥 _lastPublishedExclude 为私有实现细节），不伪造 rcArea。
        // 只在显式 --scenario ime-control-position-probe 时运行，不进默认矩阵。
        if (scenarioFilter is not null && ShouldRun(scenarioFilter, "ime-control-position-probe"))
        {
            compositionObserved |= S(log, "ime-control-position-probe", failures, () =>
            {
                if (!EnsureFieldReady(log, "ime-control-position-probe", script, ctx, bridge, hwnd, 0, ct))
                {
                    return false;
                }

                if (!script.SendAsciiText("nihao", "imectl-preedit")
                    || !WaitFor(ctx, () => bridge.ActiveClient?.IsComposing == true, 5_000, ct)
                    || !WaitFor(ctx, () => bridge.LastAnchorClient is not null, 5_000, ct))
                {
                    LogFailure(log, "ime-control-position-probe", "no-composition-or-anchor");
                    return false;
                }

                var bindBefore = bindingRead();
                var anchorBefore = bridge.LastAnchorClient;
                var compBefore = SnapshotComposition(ctx, bridge, hwnd, ct);
                var scroll0 = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                if (!WaitWithOwnForeground(hwnd, 400, ct))
                {
                    LogFailure(log, "ime-control-position-probe", "foreground-lost-before-initial-shot");
                    return false;
                }

                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s13a-initial.png"));

                // 小滚动 7 逻辑像素（与 round1/scroll-edit-diagnostic 同参数）：锚点重发、
                // 候选预期不动——随后用 IMC_SETCANDIDATEPOS 单独试一次显式定位。
                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -7,
                        KeyModifiers.None, WheelDeltaMode.Pixels));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scroll0, 5_000, ct);
                var scrollSmall = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
                var anchorAfterScroll = bridge.LastAnchorClient;
                Thread.Sleep(400);
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    LogFailure(log, "ime-control-position-probe", "foreground-lost-after-scroll");
                    return false;
                }

                ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s13b-scroll.png"));

                // 目标校验（UI 线程）：默认 IME 窗口必须与自有窗口同线程/同进程且为有效窗口。
                var target = Query(ctx, () =>
                {
                    var ownTid = NativeMethods.GetWindowThreadProcessId(hwnd, out var ownPid);
                    var imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
                    uint imeTid = 0, imePid = 0;
                    var isWindow = false;
                    if (imeWnd != IntPtr.Zero)
                    {
                        imeTid = NativeMethods.GetWindowThreadProcessId(imeWnd, out imePid);
                        isWindow = NativeMethods.IsWindow(imeWnd);
                    }

                    return (imeWnd, ownTid, ownPid, imeTid, imePid, isWindow);
                }, ct);
                var sameOwner = target.imeWnd != IntPtr.Zero
                    && target.isWindow
                    && target.imeTid == target.ownTid
                    && target.imePid == target.ownPid;
                log.Record("script", "scenario", "ime-control-target", new Dictionary<string, object?>
                {
                    ["ime_wnd"] = $"0x{target.imeWnd.ToInt64():X}",
                    ["own_tid"] = target.ownTid,
                    ["ime_tid"] = target.imeTid,
                    ["own_pid"] = target.ownPid,
                    ["ime_pid"] = target.imePid,
                    ["is_window"] = target.isWindow,
                    ["same_thread_and_process"] = sameOwner,
                });
                if (!sameOwner)
                {
                    LogFailure(log, "ime-control-position-probe", "ime-window-mismatch");
                    return false;
                }

                if (bridge.LastAnchorClient is not { } anchor)
                {
                    LogFailure(log, "ime-control-position-probe", "no-anchor-after-scroll");
                    return false;
                }

                // 官方入口：WM_IME_CONTROL / IMC_SETCANDIDATEPOS（imm.h 0x0008）。
                // CANDIDATEFORM 用 pinned 托管结构，生命周期严格覆盖同步消息窗口；
                // 同线程已由上面验证，SendMessageTimeout 仅作防御性上界。
                var sendResult = Query(ctx, () =>
                {
                    var form = new NativeMethods.CANDIDATEFORM
                    {
                        dwIndex = 0,
                        dwStyle = NativeMethods.CFS_CANDIDATEPOS,
                        ptCurrentPos = new NativeMethods.POINT { X = anchor.X, Y = anchor.Y },
                        rcArea = default,
                    };
                    var pin = GCHandle.Alloc(form, GCHandleType.Pinned);
                    try
                    {
                        var sent = NativeMethods.SendMessageTimeout(target.imeWnd,
                            NativeMethods.WM_IME_CONTROL, (IntPtr)NativeMethods.IMC_SETCANDIDATEPOS,
                            pin.AddrOfPinnedObject(), NativeMethods.SMTO_NORMAL, 2_000, out var ret);
                        return (sent != IntPtr.Zero, ret);
                    }
                    finally
                    {
                        pin.Free();
                    }
                }, ct);

                // 读回自有 HIMC 的候选定位（只读对照，不写）。
                var readback = Query(ctx, () =>
                {
                    var himc = NativeMethods.ImmGetContext(hwnd);
                    if (himc == IntPtr.Zero)
                    {
                        return (NativeMethods.CANDIDATEFORM?)null;
                    }

                    try
                    {
                        return NativeMethods.ImmGetCandidateWindow(himc, 0, out var cf)
                            ? cf
                            : (NativeMethods.CANDIDATEFORM?)null;
                    }
                    finally
                    {
                        NativeMethods.ImmReleaseContext(hwnd, himc);
                    }
                }, ct);

                Thread.Sleep(400); // 给候选窗一次重排机会再截图
                var anchorAfterProbe = bridge.LastAnchorClient;
                var compAfter = SnapshotComposition(ctx, bridge, hwnd, ct);
                var bindAfter = bindingRead();
                if (NativeMethods.GetForegroundWindow() == hwnd)
                {
                    ScreenCapture.CaptureWindowRegion(
                        hwnd, Path.Combine(outDir, "s13c-pos.png"));
                }

                // 清理：仅自有前台发 Esc 取消残留组合，滚回顶部。
                if (NativeMethods.GetForegroundWindow() == hwnd
                    && Query(ctx, () => bridge.ActiveClient?.IsComposing == true, ct))
                {
                    script.PressVirtualKey(VkEscape, "imectl-cancel");
                    WaitFor(ctx, () => bridge.ActiveClient?.IsComposing != true, 5_000, ct);
                }

                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, 100));
                    return true;
                }, ct);
                WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY == 0, 5_000, ct);

                var writesDelta = bindAfter.Writes - bindBefore.Writes;
                log.Record("script", "scenario", "ime-control-position-probe", new Dictionary<string, object?>
                {
                    ["scroll_before"] = scroll0,
                    ["scroll_small"] = scrollSmall,
                    ["anchor_before"] = anchorBefore?.ToString() ?? "(none)",
                    ["anchor_after_scroll"] = anchorAfterScroll?.ToString() ?? "(none)",
                    ["anchor_after_probe"] = anchorAfterProbe?.ToString() ?? "(none)",
                    ["imc_sent"] = sendResult.Item1,
                    ["imc_ret"] = $"0x{sendResult.ret.ToInt64():X}",
                    ["imc_ret_zero_success"] = sendResult.ret == IntPtr.Zero,
                    ["readback_style"] = readback is { } rb ? $"0x{rb.dwStyle:X}" : "(none)",
                    ["readback_pt"] = readback is { } rb2 ? $"({rb2.ptCurrentPos.X},{rb2.ptCurrentPos.Y})" : "(none)",
                    ["exclude_attempted"] = false,
                    ["exclude_note"] = "真实排除区在工具层不可读（桥私有状态），不伪造 rcArea，仅 pos 单项。",
                    ["compstr_before"] = compBefore.CompStr ?? "(none)",
                    ["compstr_after"] = compAfter.CompStr ?? "(none)",
                    ["preedit_before"] = compBefore.Preedit ?? "(none)",
                    ["preedit_after"] = compAfter.Preedit ?? "(none)",
                    ["composing_after"] = compAfter.Composing,
                    ["writes_delta"] = writesDelta,
                    ["text_unchanged"] = bindAfter.Text == bindBefore.Text,
                    ["note"] = "IMC_SETCANDIDATEPOS 返回 0 仅表示命令被受理，候选是否移动须 Codex "
                        + "核对 s13a/s13b/s13c；诊断通过不等于 R16 修复。",
                });
                // 判据：滚动真实发生 + 同主 IME 窗口消息已派发 + 组合/预编辑全程保留 + 无绑定提交。
                return scrollSmall > scroll0
                    && sendResult.Item1
                    && compAfter.Composing
                    && writesDelta == 0;
            });
        }

        // composition_expected：本次实际选中集中是否有需要中文组合的场景；
        // compositionObserved 只记录真实见证组合通过的场景（绝不人为置真冒充观测）。
        // 缺组合的中文场景自身判据必 fail → 由 failures 承载 exit 1，不靠全局统计否决。
        var compositionExpected =
            scenarioFilter is null || CompositionRequiredScenarios.Contains(scenarioFilter);
        log.Record("script", "tool", "scenarios-done", new Dictionary<string, object?>
        {
            ["scenarios_ran"] = scenariosRan,
            ["composition_expected"] = compositionExpected,
            ["any_composition_observed"] = compositionObserved,
            ["composition_required_missing"] = compositionExpected && !compositionObserved,
            ["failures"] = string.Join("|", failures),
        });
        return scenariosRan;
    }
    private static bool RunTsfContainerScroll(
        ProbeLog log, string outDir, InputScript script, UiHostContext ctx,
        WindowsImeBridge bridge, IntPtr hwnd, Func<(string Text, int Writes)> bindingRead,
        CancellationToken ct)
    {
        if (!EnsureFieldReady(log, "container-scroll", script, ctx, bridge, hwnd, 0, ct)
            || !script.SendAsciiText("nihao", "tsf-scroll-preedit")
            || !WaitFor(ctx, () => UiApplication.CurrentHost?.Router?.TsfBridge?.Session.Owner.Snapshot.IsComposing == true, 5_000, ct))
        {
            LogFailure(log, "container-scroll", "tsf-composition-not-started");
            return false;
        }

        var initial = Query(ctx, () => ReadTsfCandidateState(bindingRead().Writes), ct);
        var before = bindingRead();
        var frame = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
        if (!WaitFor(ctx, () => ctx.RenderedFrameCount > frame, 5_000, ct))
        {
            LogFailure(log, "container-scroll", "initial-frame-not-rendered");
            return false;
        }
        var scroll0 = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
        ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7a-composing.png"));

        Query(ctx, () =>
        {
            ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -7,
                KeyModifiers.None, WheelDeltaMode.Pixels));
            return true;
        }, ct);
        var smallMoved = WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scroll0, 5_000, ct);
        var scrollSmall = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
        var frameBeforeSmallCapture = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
        var smallRendered = WaitFor(ctx, () => ctx.RenderedFrameCount > frameBeforeSmallCapture, 5_000, ct);
        var small = Query(ctx, () => ReadTsfCandidateState(bindingRead().Writes), ct);
        ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7b-small-scroll.png"));

        Query(ctx, () =>
        {
            ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, -500,
                KeyModifiers.None, WheelDeltaMode.Pixels));
            return true;
        }, ct);
        var scrolledOut = WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY > scrollSmall, 5_000, ct);
        var scrollOut = Query(ctx, () => ctx.Runtime.Root.ScrollOffsetY, ct);
        var hidden = WaitFor(ctx,
            () => UiApplication.CurrentHost?.Router?.TsfBridge?.Session.GeometryProvider.Current is null,
            5_000, ct);
        var frameBeforeHiddenCapture = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
        var hiddenRendered = WaitFor(ctx, () => ctx.RenderedFrameCount > frameBeforeHiddenCapture, 5_000, ct);
        var hiddenState = Query(ctx, () => ReadTsfCandidateState(bindingRead().Writes), ct);
        var composingHidden = Query(ctx,
            () => UiApplication.CurrentHost?.Router?.TsfBridge?.Session.Owner.Snapshot.IsComposing == true, ct);
        ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7c-scrolled-out.png"));

        Query(ctx, () =>
        {
            ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, 1000,
                KeyModifiers.None, WheelDeltaMode.Pixels));
            return true;
        }, ct);
        var restoredScroll = WaitFor(ctx, () => ctx.Runtime.Root.ScrollOffsetY == 0, 5_000, ct);
        var restored = WaitFor(ctx,
            () => UiApplication.CurrentHost?.Router?.TsfBridge?.Session.GeometryProvider.Current is not null,
            5_000, ct);
        var frameBeforeRestoreCapture = Query(ctx, () => { var n = ctx.RenderedFrameCount; ctx.Runtime.InvalidateRender(); return n; }, ct);
        var restoredRendered = WaitFor(ctx, () => ctx.RenderedFrameCount > frameBeforeRestoreCapture, 5_000, ct);
        var recovered = Query(ctx, () => ReadTsfCandidateState(bindingRead().Writes), ct);
        var afterRestore = bindingRead();
        ScreenCapture.CaptureWindowRegion(hwnd, Path.Combine(outDir, "s7d-restored.png"));

        var sameIdentity = IdentityMatches(initial, small)
            && IdentityMatches(initial, hiddenState)
            && IdentityMatches(initial, recovered)
            && initial is { Preedit: "ni'hao", CompositionId: >= 0 };
        var zeroWrites = before.Text == afterRestore.Text && before.Writes == afterRestore.Writes
            && small?.SetterWrites == before.Writes
            && hiddenState?.SetterWrites == before.Writes
            && recovered?.SetterWrites == before.Writes;
        var spaceSent = NativeMethods.GetForegroundWindow() == hwnd
            && script.PressVirtualKey(VkSpace, "tsf-scroll-commit-space");
        var committed = spaceSent && WaitFor(bindingRead, b => b.Writes > before.Writes, 8_000, ct);
        var afterCommit = bindingRead();
        var commitExactOnce = committed && afterCommit.Text == before.Text + "你好"
            && afterCommit.Writes - before.Writes == 1;
        log.Record("script", "scenario", "tsf-container-scroll-result", new Dictionary<string, object?>
        {
            ["scroll_before"] = scroll0,
            ["scroll_small"] = scrollSmall,
            ["scroll_out"] = scrollOut,
            ["small_moved"] = smallMoved,
            ["small_frame_rendered"] = smallRendered,
            ["scrolled_out"] = scrolledOut,
            ["hidden_geometry_cleared"] = hidden,
            ["hidden_frame_rendered"] = hiddenRendered,
            ["composition_preserved_hidden"] = composingHidden,
            ["restore_scroll_top"] = restoredScroll,
            ["geometry_restored"] = restored,
            ["restored_frame_rendered"] = restoredRendered,
            ["identity_same_session_composition_element_generation_preedit"] = sameIdentity,
            ["writes_unchanged_until_commit"] = zeroWrites,
            ["final_text"] = afterCommit.Text,
            ["expected_text"] = before.Text + "你好",
            ["writes_delta"] = afterCommit.Writes - before.Writes,
            ["commit_exactly_once"] = commitExactOnce,
            ["initial"] = initial?.ToLog(),
            ["small"] = small?.ToLog(),
            ["hidden"] = hiddenState?.ToLog(),
            ["restored"] = recovered?.ToLog(),
        });
        return smallMoved && smallRendered && scrolledOut && hidden && hiddenRendered
            && composingHidden && restoredScroll && restored && restoredRendered
            && sameIdentity && zeroWrites && commitExactOnce;
    }

    private sealed record TsfCandidateDiagnostic(
        int CompositionSessionId, int CompositionId, int SetterWrites,
        bool IsComposing, string Preedit, uint ActiveFlags, bool UiElementAdvised,
        bool CandidatePresentationAvailable, uint CurrentElementId, uint CurrentGeneration,
        bool CandidateValid, string? CandidateFailure, string[] CandidateTexts,
        int SelectionOnPage, uint SelectionIndex, string Geometry, string Counters,
        bool SessionFocused, int RuntimeLayoutSerial)
    {
        internal Dictionary<string, object?> ToLog() => new()
        {
            ["composition_session_id"] = CompositionSessionId,
            ["composition_id"] = CompositionId,
            ["setter_writes"] = SetterWrites,
            ["is_composing"] = IsComposing,
            ["preedit"] = Preedit,
            ["ui_element_advised"] = UiElementAdvised,
            ["candidate_presentation_available"] = CandidatePresentationAvailable,
            ["current_element_id"] = CurrentElementId,
            ["current_generation"] = CurrentGeneration,
            ["candidate_valid"] = CandidateValid,
            ["candidate_failure"] = CandidateFailure,
            ["candidate_texts"] = CandidateTexts,
            ["selection_on_page"] = SelectionOnPage,
            ["selection_index"] = SelectionIndex,
            ["geometry"] = Geometry,
            ["geometry_counters"] = Counters,
            ["session_focused"] = SessionFocused,
            ["runtime_layout_serial"] = RuntimeLayoutSerial,
        };
    }

    private static TsfCandidateDiagnostic? ReadTsfCandidateState(int setterWrites = 0)
    {
        var host = UiApplication.CurrentHost;
        var router = host?.Router;
        var tsf = router?.TsfBridge;
        var session = tsf?.Session;
        if (session is null) return null;
        var identity = tsf?.CompositionIdentity;
        var compositionId = identity?.CompositionId ?? -1;
        var compositionSessionId = identity?.SessionId ?? -1;
        var candidate = session.UiElementSink?.LastPublished;
        var geometry = session.GeometryProvider.Current;
        var geometryDescription = geometry is null
            ? "unavailable"
            : $"{geometry.Logical}; client={geometry.ClientScreen}; scale={geometry.PixelScale}; "
                + $"scroll={geometry.ScrollX}; epoch={geometry.Epoch}; layout={geometry.LayoutSerial}; storeVersion={geometry.StoreDocumentVersion}; "
                + $"nativeGeneration={geometry.NativeGeneration}; viewport={host!.Context.LogicalSize}";
        var counts = tsf is null ? "unavailable"
            : $"publish={tsf.GeometryPublishCount}; rejected={tsf.GeometryRejectedCount}; cleared={tsf.GeometryClearedCount}; layoutNotifications={tsf.LayoutNotificationCount}";
        return new TsfCandidateDiagnostic(
            compositionSessionId, compositionId, setterWrites,
            identity?.IsComposing ?? false, session.Owner.Snapshot.Preedit,
            session.ActiveFlags, session.UiElementAdvised, session.CandidatePresentationAvailable,
            session.UiElementSink?.CurrentElementId ?? 0,
            session.UiElementSink?.CurrentGeneration ?? 0,
            candidate?.Valid ?? false, candidate?.Failure,
            candidate?.Entries.Select(entry => entry.Text).ToArray() ?? Array.Empty<string>(),
            candidate?.SelectionOnPage ?? -1, candidate?.SelectionIndex ?? uint.MaxValue,
            geometryDescription, counts, session.IsFocused, geometry?.LayoutSerial ?? -1);
    }
    private static bool IdentityMatches(TsfCandidateDiagnostic? first, TsfCandidateDiagnostic? next)
        => first is not null && next is not null
            && first.IsComposing && next.IsComposing
            && first.CompositionSessionId == next.CompositionSessionId
            && first.CompositionId >= 0 && first.CompositionId == next.CompositionId
            && first.CurrentElementId == next.CurrentElementId
            && first.CurrentGeneration == next.CurrentGeneration
            && string.Equals(first.Preedit, next.Preedit, StringComparison.Ordinal)
            && first.SetterWrites == next.SetterWrites;

    /// <summary>需要真实中文组合才能通过的场景名集合——ascii-plain/unicode-direct 不在其中，
    /// 因此仅运行它们时“无组合”不构成否决条件。</summary>
    private static readonly HashSet<string> CompositionRequiredScenarios = new(StringComparer.Ordinal)
    {
        "nihao-commit", "escape-cancel", "enter-select", "segmented", "digit-select",
        "focus-switch-cancel", "container-scroll", "breakpoint-reflow", "anchor-follow",
        "minimize-restore", "hide-restore", "window-deactivate", "horizontal-scroll", "disabled-unmount", "resize-samebranch", "external-update-cancel", "undo-redo",
        "close-window", "candidate-visibility-probe", "native-caret-probe",
        "native-caret-early-probe", "scroll-edit-diagnostic",
        "ime-control-position-probe",
    };

    /// <summary>进程/宿主退出码纯函数（脚本判定与 host exit code 同源）：
    /// 2=环境不可用；1=有失败/宿主异常/过滤后 0 场景；0=全部通过。
    /// 单场景结果权威——英文/Unicode 过滤运行无组合也可 exit 0。</summary>
    internal static int ComputeExitCode(
        bool envUnavailable, bool hostFaulted, int failureCount, int scenariosRan)
    {
        if (envUnavailable)
        {
            return 2;
        }

        if (failureCount > 0 || hostFaulted || scenariosRan == 0)
        {
            return 1;
        }

        return 0;
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>
    /// 原子注入 Ctrl+键（单个 SendInput 批次完成 down/down/up/up），
    /// 避免分批注入在两次调用之间丢前台导致修饰键悬挂。
    /// </summary>
    private static bool SendChord(IntPtr hwnd, ushort modifier, ushort key)
    {
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            return false;
        }

        var down = InputScript.BuildKeyboardInput(modifier, 0, 0)
            .Concat(InputScript.BuildKeyboardInput(key, 0, 0))
            .Concat(InputScript.BuildKeyboardInput(key, 0, NativeMethods.KEYEVENTF_KEYUP))
            .Concat(InputScript.BuildKeyboardInput(modifier, 0, NativeMethods.KEYEVENTF_KEYUP))
            .ToArray();
        return NativeMethods.SendInput(4, down, down.Length / 4) == 4;
    }

    /// <summary>在 UI 线程快照自有 HIMC 的组合串与桥状态（候选诊断用）；成对 GetContext/ReleaseContext。</summary>
    private static (bool HimcOk, int CompLen, string? CompStr, bool Composing, string? Preedit)
        SnapshotComposition(UiHostContext ctx, WindowsImeBridge bridge, IntPtr hwnd, CancellationToken ct)
        => Query(ctx, () =>
        {
            var himc = NativeMethods.ImmGetContext(hwnd);
            string? comp = null;
            var compLen = -1;
            var himcOk = himc != IntPtr.Zero;
            if (himcOk)
            {
                try
                {
                    compLen = NativeMethods.ImmGetCompositionStringW(
                        himc, NativeMethods.GCS_COMPSTR, null, 0);
                    if (compLen > 0)
                    {
                        var buf = new byte[compLen];
                        var read = NativeMethods.ImmGetCompositionStringW(
                            himc, NativeMethods.GCS_COMPSTR, buf, compLen);
                        if (read > 0 && read <= compLen)
                        {
                            comp = System.Text.Encoding.Unicode.GetString(buf, 0, read);
                        }
                    }
                }
                finally
                {
                    NativeMethods.ImmReleaseContext(hwnd, himc);
                }
            }

            return (himcOk, compLen, comp,
                bridge.ActiveClient?.IsComposing == true, bridge.ActiveClient?.Preedit);
        }, ct);

    /// <summary>隐藏 caret 创建守卫：GUIThreadInfo 查询必须成功（失败视同状态未知，
    /// 不允许当作无 caret 继续），且自有 HWND 同时持队列焦点与前台、无线程 caret。</summary>
    internal static bool ShouldCreateHiddenCaret(
        bool guiInfoOk, IntPtr existingCaretOwner, IntPtr focus, IntPtr foreground, IntPtr hwnd)
        => guiInfoOk
            && focus == hwnd
            && foreground == hwnd
            && existingCaretOwner == IntPtr.Zero;

    /// <summary>有界前台守卫等待：只读轮询自有前台；期间前台丢失返回 false
    /// （调用方停止实验走 finally 清理），绝不抢前台。</summary>
    private static bool WaitWithOwnForeground(IntPtr hwnd, int totalMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + totalMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>原子创建守卫 + CreateCaret(1x1 隐藏，绝不 ShowCaret) + SetCaretPos 到
    /// 当前桥锚点 + GetCaretPos 回读；全部在 UI 线程一次 Query 内执行并返回守卫读数。
    /// GUIThreadInfo 查询失败视同状态未知——不当成无 caret 继续创建。</summary>
    private static (bool Created, bool GuiOk, IntPtr Focus, IntPtr Foreground,
        IntPtr ExistingCaret, bool CaretBlinking, bool SetPos, bool GetPosOk,
        NativeMethods.POINT Pos)
        TryCreateHiddenCaret(UiHostContext ctx, IntPtr hwnd, WindowsImeBridge bridge,
            CancellationToken ct)
        => Query(ctx, () =>
        {
            var focus = NativeMethods.GetFocus();
            var foreground = NativeMethods.GetForegroundWindow();
            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            var guiOk = NativeMethods.GetGUIThreadInfo(0, ref info);
            var existingCaret = guiOk ? info.hwndCaret : IntPtr.Zero;
            if (!ShouldCreateHiddenCaret(guiOk, existingCaret, focus, foreground, hwnd))
            {
                return (false, guiOk, focus, foreground, existingCaret,
                    guiOk && (info.flags & NativeMethods.GUI_CARETBLINKING) != 0,
                    false, false, default);
            }

            var created = NativeMethods.CreateCaret(hwnd, IntPtr.Zero, 1, 1);
            var setPos = false;
            var getPosOk = false;
            var pos = default(NativeMethods.POINT);
            if (created && bridge.LastAnchorClient is { } a)
            {
                setPos = NativeMethods.SetCaretPos(a.X, a.Y);
                getPosOk = NativeMethods.GetCaretPos(out pos);
            }

            return (created, guiOk, focus, foreground, existingCaret,
                guiOk && (info.flags & NativeMethods.GUI_CARETBLINKING) != 0,
                setPos, getPosOk, pos);
        }, ct);

    /// <summary>caret 探针统一收尾：只销毁本实验创建且当前仍归属自有 HWND 的 caret
    /// （GUIThreadInfo 复核；所有权已转移记 lost_ownership 不销毁新所有者）→ 自有
    /// 前台仍在才 Esc 取消残留组合 → 滚回顶部。前台丢失不抢前台；宿主已退出则跳过。</summary>
    private static void CleanupCaretProbe(
        ProbeLog log, string name, UiHostContext ctx, InputScript script,
        WindowsImeBridge bridge, IntPtr hwnd, bool caretCreated, ManualResetEventSlim hostExited)
    {
        if (caretCreated && NativeMethods.IsWindow(hwnd) && !hostExited.IsSet)
        {
            try
            {
                var destroy = Query(ctx, () =>
                {
                    var info = new NativeMethods.GUITHREADINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
                    };
                    var guiOk = NativeMethods.GetGUIThreadInfo(0, ref info);
                    if (!guiOk || info.hwndCaret != hwnd)
                    {
                        return (Destroyed: false, GuiOk: guiOk,
                            Owner: guiOk ? info.hwndCaret : IntPtr.Zero);
                    }

                    return (Destroyed: NativeMethods.DestroyCaret(),
                        GuiOk: guiOk, Owner: info.hwndCaret);
                }, CancellationToken.None);
                log.Record("script", "scenario", "caret-destroyed",
                    new Dictionary<string, object?>
                    {
                        ["scenario"] = name,
                        ["ok"] = destroy.Destroyed,
                        ["gui_info_ok"] = destroy.GuiOk,
                        ["caret_owner"] = $"0x{destroy.Owner.ToInt64():X}",
                        ["lost_ownership"] = !destroy.Destroyed
                            && destroy.GuiOk && destroy.Owner != hwnd,
                    });
            }
            catch (Exception ex)
            {
                log.Record("script", "scenario", "caret-destroy-fault",
                    new Dictionary<string, object?>
                    {
                        ["scenario"] = name,
                        ["exception"] = ex.GetType().Name,
                    });
            }
        }

        if (NativeMethods.GetForegroundWindow() == hwnd && !hostExited.IsSet)
        {
            try
            {
                if (Query(ctx, () => bridge.ActiveClient?.IsComposing == true,
                        CancellationToken.None)
                    && script.PressVirtualKey(VkEscape, $"{name}-cancel"))
                {
                    WaitFor(ctx, () => bridge.ActiveClient?.IsComposing != true,
                        5_000, CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // 宿主收尾中：清理尽力而为，如实留给 finish 日志核对。
            }
        }

        if (NativeMethods.IsWindow(hwnd) && !hostExited.IsSet)
        {
            try
            {
                Query(ctx, () =>
                {
                    ctx.Runtime.DispatchWheel(new WheelEvent(new Point(200, 60), 0, 100));
                    return true;
                }, CancellationToken.None);
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }

    /// <summary>在 UI 线程对自有 HIMC 调用一次 ImmNotifyIME，成对 Get/ReleaseContext；
    /// 返回（是否拿到HIMC并调用， API返回值）。</summary>
    private static (bool Called, bool Ok) NotifyIme(
        UiHostContext ctx, IntPtr hwnd, uint action, CancellationToken ct)
        => Query(ctx, () =>
        {
            var himc = NativeMethods.ImmGetContext(hwnd);
            if (himc == IntPtr.Zero)
            {
                return (false, false);
            }

            try
            {
                return (true, NativeMethods.ImmNotifyIME(himc, action, 0, 0));
            }
            finally
            {
                NativeMethods.ImmReleaseContext(hwnd, himc);
            }
        }, ct);

    /// <summary>
    /// 只读等待确认键（F10）完成一次“自有前台按下→松开”：只接受前台归属本窗口时由松到按的边沿，
    /// 按住期间前台丢失则该次按下作废；松开后再次校验前台仍属本窗口才确认。
    /// 不注入任何输入；看门狗取消抛 <see cref="OperationCanceledException"/>，宿主退出/超时返回 false。
    /// </summary>
    private static bool WaitForConfirmKey(
        IntPtr hwnd, int virtualKey, int timeoutMs, CancellationToken ct, ManualResetEventSlim hostExited)
        => WaitForConfirmKey(hwnd, virtualKey, timeoutMs, ct, hostExited,
            NativeMethods.GetAsyncKeyState, NativeMethods.GetForegroundWindow);

    /// <summary>确认守卫核心逻辑；keyState/foreground 可注入以便无 GUI 自检。</summary>
    private static bool WaitForConfirmKey(
        IntPtr hwnd, int virtualKey, int timeoutMs, CancellationToken ct, ManualResetEventSlim hostExited,
        Func<int, short> keyState, Func<IntPtr> foreground)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        // 起始已按住的键不构成确认沿：先采样一次，只认其后由松到按的转换。
        var wasDown = (keyState(virtualKey) & unchecked((short)0x8000)) != 0;
        var confirming = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (hostExited.IsSet)
            {
                return false;
            }

            var isDown = (keyState(virtualKey) & unchecked((short)0x8000)) != 0;
            var ownForeground = foreground() == hwnd;
            if (!confirming)
            {
                if (!wasDown && isDown && ownForeground)
                {
                    confirming = true;
                }
            }
            else if (!isDown)
            {
                // 松开后的前台复核：按下到松开之间前台若已丢失，不算确认。
                return foreground() == hwnd;
            }
            else if (!ownForeground)
            {
                confirming = false;
            }

            wasDown = isDown;
            Thread.Sleep(30);
        }

        return false;
    }

    /// <summary>
    /// --selftest：对确认守卫做无 GUI 自检——注入键状态/前台探针覆盖取消、超时、他窗前台的 F10、
    /// 起始按住、按住中丢前台、松开重按等情形。不启动宿主、不发送任何输入。全部通过返回 true。
    /// </summary>
    private static bool SelfTest(ProbeLog log)
    {
        var pass = true;
        var hwnd = new IntPtr(0x1000);
        var foreign = new IntPtr(0x2000);
        var exited = new ManualResetEventSlim();
        void Check(string name, bool cond, string detail)
        {
            pass &= cond;
            log.Record("script", "selftest", "check", new Dictionary<string, object?>
            {
                ["name"] = name,
                ["pass"] = cond,
                ["detail"] = detail,
            });
            Console.WriteLine($"selftest {name}: {(cond ? "pass" : "FAIL")} {detail}");
        }

        // 1) 超时且从未按下 → false（调用方据此不发键）。
        var calls = 0;
        var timeoutRes = WaitForConfirmKey(hwnd, VkF10, 250, CancellationToken.None, exited,
            _ => { calls++; return 0; }, () => hwnd);
        Check("timeout-never-pressed", !timeoutRes, $"keypolls={calls}");

        // 2) 取消 → OperationCanceledException（不吞、不继续）。
        var cts = new CancellationTokenSource();
        var threwOce = false;
        try
        {
            WaitForConfirmKey(hwnd, VkF10, 30_000, cts.Token, exited,
                _ => { cts.Cancel(); return 0; }, () => hwnd);
        }
        catch (OperationCanceledException)
        {
            threwOce = true;
        }

        Check("cancel-throws-oce", threwOce, "");

        // 3) 宿主已退出 → 立即 false。
        var exitedSet = new ManualResetEventSlim(true);
        var exitedRes = WaitForConfirmKey(hwnd, VkF10, 5_000, CancellationToken.None, exitedSet,
            _ => 0, () => hwnd);
        Check("host-exited-false", !exitedRes, "");

        // 4) 自有前台按下并松开 → true。
        var i4 = 0;
        var okRes = WaitForConfirmKey(hwnd, VkF10, 5_000, CancellationToken.None, exited,
            _ => unchecked((short)(i4++ is >= 3 and < 6 ? 0x8000 : 0)), () => hwnd);
        Check("own-foreground-press-release", okRes, "");

        // 5) 前台是他窗时按下并松开 → 忽略 → 超时 false（其他应用的 F10 不算确认）。
        var i5 = 0;
        var foreignRes = WaitForConfirmKey(hwnd, VkF10, 500, CancellationToken.None, exited,
            _ => unchecked((short)(i5++ is >= 2 and < 5 ? 0x8000 : 0)), () => foreign);
        Check("foreign-foreground-ignored", !foreignRes, "");

        // 6) 按下期间前台丢失、松开后仍他窗 → 不确认 → 超时 false。
        var i6 = 0;
        var lostRes = WaitForConfirmKey(hwnd, VkF10, 600, CancellationToken.None, exited,
            _ => unchecked((short)(i6++ is >= 2 and < 8 ? 0x8000 : 0)),
            () => i6 < 8 ? hwnd : foreign);
        Check("foreground-lost-while-held", !lostRes, "");

        // 7) 等待开始前已按住 → 不是边沿 → 超时 false。
        var heldRes = WaitForConfirmKey(hwnd, VkF10, 250, CancellationToken.None, exited,
            _ => unchecked((short)0x8000), () => hwnd);
        Check("held-at-start-ignored", !heldRes, "");

        // 8) 起始按住 → 松开 → 自有前台重新按下并松开 → true（新边沿有效）。
        var i8 = 0;
        var repress = WaitForConfirmKey(hwnd, VkF10, 5_000, CancellationToken.None, exited,
            _ => unchecked((short)(i8++ is < 2 or (>= 5 and < 8) ? 0x8000 : 0)), () => hwnd);
        Check("repress-after-release", repress, "");

        // 9) NI_* 常量与本机 SDK imm.h (10.0.26100.0) 一致。
        Check("ni-constants",
            NativeMethods.NI_OPENCANDIDATE == 0x0010 && NativeMethods.NI_CLOSECANDIDATE == 0x0011,
            "imm.h");

        // 10) caret 创建守卫：guiOk + 无既有 caret + 自有焦点 + 自有前台才可创建；
        //    查询失败/已有 caret/非本窗焦点/非本窗前台均拒绝。
        Check("caret-create-guard",
            ShouldCreateHiddenCaret(true, IntPtr.Zero, hwnd, hwnd, hwnd)
            && !ShouldCreateHiddenCaret(false, IntPtr.Zero, hwnd, hwnd, hwnd)
            && !ShouldCreateHiddenCaret(true, foreign, hwnd, hwnd, hwnd)
            && !ShouldCreateHiddenCaret(true, IntPtr.Zero, foreign, hwnd, hwnd)
            && !ShouldCreateHiddenCaret(true, IntPtr.Zero, hwnd, foreign, hwnd)
            && !ShouldCreateHiddenCaret(true, foreign, foreign, foreign, hwnd),
            "guiinfo-fail/existing-caret/foreign-focus/foreign-foreground rejected");

        // 11) 退出码纯函数：环境不可用→2；有失败/宿主异常/0 场景→1；全过→0。
        //    英文/Unicode 单场景成功（无组合要求）→0；不拿“缺中文组合”否决已通过场景。
        Check("exitcode-env-unavailable",
            ComputeExitCode(envUnavailable: true, hostFaulted: false, failureCount: 0, scenariosRan: 0) == 2,
            "env-missing -> 2");
        Check("exitcode-scenario-failure",
            ComputeExitCode(false, false, failureCount: 1, scenariosRan: 1) == 1,
            "failure -> 1");
        Check("exitcode-ascii-or-unicode-ok",
            ComputeExitCode(false, false, failureCount: 0, scenariosRan: 1) == 0,
            "english/unicode pass without composition -> 0");
        Check("exitcode-all-with-composition-ok",
            ComputeExitCode(false, false, failureCount: 0, scenariosRan: 16) == 0,
            "full matrix pass -> 0");
        Check("exitcode-zero-scenarios",
            ComputeExitCode(false, false, failureCount: 0, scenariosRan: 0) == 1,
            "filtered-to-nothing -> 1");
        Check("exitcode-host-fault",
            ComputeExitCode(false, hostFaulted: true, failureCount: 0, scenariosRan: 3) == 1,
            "host fault -> 1");

        log.Record("script", "selftest", "done", new Dictionary<string, object?>
        {
            ["all_passed"] = pass,
        });
        Console.WriteLine($"selftest done: {(pass ? "all pass" : "FAILURES")}");
        return pass;
    }

    /// <summary>轮询等待自有窗口成为前台（用户点击/激活）；只读前台归属，不发送任何输入。</summary>
    private static bool WaitForUserForeground(IntPtr hwnd, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            if (NativeMethods.GetForegroundWindow() == hwnd)
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>场景名匹配过滤器；--scenario 指定时只跑该场景（环境与会话建立仍照常）。</summary>
    private static bool ShouldRun(string? filter, string name)
        => filter is null || string.Equals(filter, name, StringComparison.OrdinalIgnoreCase);

    private static bool Scenario(ProbeLog log, string name, List<string> failures, Func<bool> run)
    {
        bool ok;
        try
        {
            ok = run();
        }
        catch (OperationCanceledException)
        {
            // 取消不吞掉、不继续发按键：抛回 RunScenarios 让 worker 收尾停止。
            log.Record("script", "scenario", "cancelled", new Dictionary<string, object?>
            {
                ["scenario"] = name,
            });
            throw;
        }
        catch (Exception ex)
        {
            ok = false;
            log.Record("script", "scenario", "fault", new Dictionary<string, object?>
            {
                ["scenario"] = name,
                ["exception"] = ex.GetType().Name,
                ["detail"] = ex.Message,
            });
        }

        log.Record("script", "scenario", ok ? "pass" : "fail", new Dictionary<string, object?>
        {
            ["scenario"] = name,
        });
        if (!ok)
        {
            failures.Add(name);
        }

        return ok;
    }

    private static T? WaitFor<T>(ProbeLog log, string label, Func<T?> probe, int timeoutMs, CancellationToken ct = default) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            ct.ThrowIfCancellationRequested();
            Thread.Sleep(50);
        }

        log.Record("script", "tool", "wait-timeout", new Dictionary<string, object?> { ["label"] = label });
        return null;
    }

    /// <summary>在 UI 线程上求值（经调度器投递），避免从脚本线程直接读运行时状态。</summary>
    private static bool WaitFor(UiHostContext ctx, Func<bool> probe, int timeoutMs, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (Query(ctx, probe, ct, Math.Max(1, (int)(deadline - Environment.TickCount64))))
                {
                    return true;
                }
            }
            catch (TimeoutException)
            {
                return false;
            }

            ct.ThrowIfCancellationRequested();
            Thread.Sleep(40);
        }

        return false;
    }

    /// <summary>直接轮询绑定见证（纯托管字段快照；setter 计数即提交证据）。</summary>
    private static bool WaitFor(Func<(string Text, int Writes)> binding, Func<(string Text, int Writes), bool> pred, int timeoutMs, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (pred(binding()))
            {
                return true;
            }

            ct.ThrowIfCancellationRequested();
            Thread.Sleep(40);
        }

        return false;
    }

    /// <summary>
    /// 在 UI 线程上求值；有界等待：宿主不再处理投递（退出/卡死）时不再无限阻塞，
    /// 脚本取消（看门狗）时立刻抛 <see cref="OperationCanceledException"/> 让 worker 收尾停止。
    /// </summary>
    private static T Query<T>(UiHostContext ctx, Func<T> probe, CancellationToken ct = default, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Dispatcher.Post(() =>
        {
            try
            {
                tcs.SetResult(probe());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        var deadline = Environment.TickCount64 + timeoutMs;
        while (!tcs.Task.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"UI 调度未在 {timeoutMs}ms 内完成（宿主已停止处理投递）。");
            }

            Thread.Sleep(10);
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    private static void LogFailure(ProbeLog log, string scenario, string reason)
        => log.Record("script", "scenario", "failure", new Dictionary<string, object?>
        {
            ["scenario"] = scenario,
            ["reason"] = reason,
        });

    private static string DescribeApartment()
    {
        try
        {
            var hr = NativeMethods.CoGetApartmentType(out var apt, out var qual);
            return $"hr=0x{hr:X8} type={apt} qualifier={qual}";
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
    /// <summary>ascii-plain（--english-keyboard）：选择既有已启用的英文键盘布局 TIP（en-US 主 LangId 0x0409）。
    /// 全在 UI 线程执行——HKL 与 TSF profile 都是线程作用域，实际 SendInput 目标是 UI 线程队列。
    /// 流程：GetKeyboardLayoutList 枚举已启用布局取 en-US HKL → ActivateKeyboardLayout 换本（UI）线程布局 →
    /// GetProfile(KEYBOARDLAYOUT,0x0409,hkl) 核对 TF_IPP_FLAG_ENABLED → ActivateProfile(FORPROCESS) →
    /// GetActiveProfile 回读 Type/LangId/HKL 全对。原活动 profile（Type/LangId/Clsid/Guid/Hkl）与原 HKL 在改动前
    /// 快照，由 <see cref="RestoreEnglishInput"/> 在收尾恢复并回读核对。任一前置不成立即如实失败不发键；
    /// 部分失败时先尽力恢复再返回 false。</summary>
    private static bool SelectEnglishKeyboardLayout(ProbeLog log, UiHostContext ctx, CancellationToken ct)
    {
        _englishRestore = null;
        var ok = Query(ctx, () =>
        {
            var fields = new Dictionary<string, object?>();
            var uiTid = (uint)Environment.CurrentManagedThreadId;
            var beforeHkl = GetKeyboardLayout(0);
            fields["ui_managed_tid"] = uiTid;
            fields["original_hkl"] = $"0x{beforeHkl.ToInt64():X}";

            // 原活动 TSF profile 快照（恢复目标；profile 可能为键盘布局或输入处理器）。
            var (hrActive, original) = TsfProfile.ReadActiveProfile(log, "english-original-profile");
            if (hrActive < 0)
            {
                fields["original_profile_read_hresult"] = $"0x{hrActive:X8}";
                log.Record("script", "tsf", "english-keyboard-unavailable", fields);
                return false; // 拿不到原身份就不能安全恢复——不发键。
            }
            fields["original_profile_type"] = original.ProfileTypeName;
            fields["original_profile_langid"] = $"0x{original.LangId:X4}";
            fields["original_profile_clsid"] = original.Clsid.ToString();
            fields["original_profile_guid"] = original.ProfileGuid.ToString();
            fields["original_profile_hkl"] = $"0x{original.Hkl.ToInt64():X}";
            _englishRestore = (beforeHkl, original);

            // 枚举已启用布局，取 en-US 主 LangId 0x0409 的实际 HKL。
            var count = GetKeyboardLayoutList(0, null);
            var list = new IntPtr[Math.Max(count, 0)];
            GetKeyboardLayoutList(count, list);
            var target = list.FirstOrDefault(h => (h.ToInt64() & 0xFFFF) == 0x0409);
            fields["layout_count"] = count;
            fields["hkls"] = list.Select(h => $"0x{h.ToInt64():X}").ToArray();
            fields["target_hkl"] = target == IntPtr.Zero ? "(none)" : $"0x{target.ToInt64():X}";
            if (target == IntPtr.Zero)
            {
                log.Record("script", "tsf", "english-keyboard-unavailable", fields);
                return false; // 无已启用英文布局——环境边界。
            }

            // HKL 层切换（UI 线程），readback 核对。
            var hklResult = ActivateKeyboardLayout(target, 0);
            var hklAfter = GetKeyboardLayout(0);
            fields["activate_result_hkl"] = $"0x{hklResult.ToInt64():X}";
            fields["readback_hkl"] = $"0x{hklAfter.ToInt64():X}";
            fields["verified_hkl"] = hklAfter == target;
            if (hklAfter != target)
            {
                log.Record("script", "tsf", "english-keyboard-unavailable", fields);
                return false;
            }

            // TSF profile 层：GetProfile 核对 enabled，ActivateProfile(FORPROCESS)，GetActiveProfile 全对。
            var tsfOk = ActivateEnglishTsfProfile(log, target, fields);
            fields["verified_active_english"] = tsfOk;
            log.Record("script", "tsf", tsfOk ? "english-keyboard-selected" : "english-keyboard-unavailable", fields);
            if (!tsfOk)
            {
                RestoreEnglishInputCore(log); // 部分失败先尽力恢复
                return false;
            }
            return true;
        }, ct);
        return ok;
    }

    /// <summary>ascii-plain（--english-keyboard）收尾：恢复原活动 TSF profile + 原 HKL，全部回读核对。
    /// 与 Select 同线程（UI 线程）经 Query 执行；未快照到原身份时不发恢复（绝不向 0/他 profile 恢复）。</summary>
    private static bool RestoreEnglishInput(ProbeLog log, UiHostContext ctx, CancellationToken ct)
    {
        if (_englishRestore is null)
        {
            return true; // 未取得原身份=未做任何变更，如实无须恢复。
        }
        return Query(ctx, () => RestoreEnglishInputCore(log), ct);
    }

    /// <summary>UI 线程内实际恢复：反向激活原 profile + 还原 HKL，逐步回读核对并记录。</summary>
    private static bool RestoreEnglishInputCore(ProbeLog log)
    {
        var saved = _englishRestore;
        _englishRestore = null; // 恢复义务已接管，允许重入安全。
        if (saved is null)
        {
            return true;
        }
        var (originalHkl, originalProfile) = saved.Value;
        var fields = new Dictionary<string, object?>
        {
            ["original_hkl"] = $"0x{originalHkl.ToInt64():X}",
            ["original_type"] = originalProfile.ProfileTypeName,
            ["original_langid"] = $"0x{originalProfile.LangId:X4}",
            ["original_clsid"] = originalProfile.Clsid.ToString(),
            ["original_guid"] = originalProfile.ProfileGuid.ToString(),
        };
        var clsid = TsfProfile.ClsidInputProcessorProfiles;
        var iid = TsfProfile.IidInputProcessorProfileMgr;
        var hrCreate = CoCreateInstance(ref clsid, IntPtr.Zero, 0x1, ref iid, out var mgr);
        var ok = true;
        if (hrCreate >= 0 && mgr != IntPtr.Zero)
        {
            try
            {
                var cat = TsfProfile.GuidTfCatTipKeyboard;
                var cls = originalProfile.Clsid;
                var guid = originalProfile.ProfileGuid;
                var hkl = originalProfile.Hkl != IntPtr.Zero ? originalProfile.Hkl : originalProfile.SubstituteHkl;
                var hr = Method<ProfileActivateDelegate>(mgr, 3)(
                    mgr, originalProfile.ProfileType, originalProfile.LangId, ref cls, ref guid, hkl,
                    TsfProfile.TfIppmForProcess);
                fields["restore_activate_hresult"] = $"0x{hr:X8}";
                var hrVer = Method<ProfileGetActiveDelegate>(mgr, 10)(mgr, ref cat, out var now);
                var same = hrVer >= 0 && SameProfile(now, originalProfile);
                fields["restore_active_hresult"] = $"0x{hrVer:X8}";
                fields["restore_active_verified"] = same;
                fields["active_after_type"] = now.ProfileTypeName;
                fields["active_after_langid"] = $"0x{now.LangId:X4}";
                fields["active_after_clsid"] = now.Clsid.ToString();
                ok = ok && hr == 0 && same;
            }
            finally
            {
                Marshal.Release(mgr);
            }
        }
        else
        {
            fields["restore_cocreate_hresult"] = $"0x{hrCreate:X8}";
            ok = false;
        }
        var hklBack = ActivateKeyboardLayout(originalHkl, 0);
        var hklAfter = GetKeyboardLayout(0);
        fields["restore_hkl_result"] = $"0x{hklBack.ToInt64():X}";
        fields["restore_hkl_readback"] = $"0x{hklAfter.ToInt64():X}";
        fields["restore_hkl_verified"] = hklAfter == originalHkl;
        ok = ok && hklAfter == originalHkl;
        log.Record("script", "tsf", "english-input-restored", fields);
        return ok;
    }

    private static bool SameProfile(in TsfProfile.InputProcessorProfile a, in TsfProfile.InputProcessorProfile b)
        => a.ProfileType == b.ProfileType && a.LangId == b.LangId
            && a.Clsid == b.Clsid && a.ProfileGuid == b.ProfileGuid && a.Hkl == b.Hkl;

    /// <summary>ascii-plain 专属：以 TF_PROFILETYPE_KEYBOARDLAYOUT + HKL FORPROCESS 激活已启用的
    /// en-US 键盘布局 TIP，并用 GetActiveProfile 回读（type/langid/HKL 全对）才算成功。
    /// 与 TsfProfile.ActivateForProcess 同一契约：S_FALSE=未启用即失败，异常如实失败，不启用不改默认。
    /// 在 UI 线程执行（TSF profile/HKL 为线程作用域）。</summary>
    private static bool ActivateEnglishTsfProfile(ProbeLog log, IntPtr hkl, IDictionary<string, object?> fields)
    {
        var clsid = TsfProfile.ClsidInputProcessorProfiles;
        var iid = TsfProfile.IidInputProcessorProfileMgr;
        var hrCreate = CoCreateInstance(ref clsid, IntPtr.Zero, 0x1, ref iid, out var mgr);
        if (hrCreate < 0 || mgr == IntPtr.Zero)
        {
            fields["tsf_cocreate_hresult"] = $"0x{hrCreate:X8}";
            return false;
        }
        try
        {
            var empty = Guid.Empty;
            var cat = TsfProfile.GuidTfCatTipKeyboard;
            // GetProfile(type=KEYBOARDLAYOUT, langid, empty, empty, hkl) 核对已启用（flags 0x2）。
            var hrGet = Method<ProfileGetDelegate>(mgr, 5)(
                mgr, TsfProfile.TfProfileTypeKeyboardLayout, 0x0409, ref empty, ref empty, hkl, out var info);
            fields["get_profile_hresult"] = $"0x{hrGet:X8}";
            fields["get_profile_flags"] = $"0x{info.Flags:X}";
            fields["profile_enabled"] = hrGet >= 0 && (info.Flags & 0x2) != 0;
            if (hrGet < 0 || (info.Flags & 0x2) == 0)
            {
                return false;
            }
            var hrAct = Method<ProfileActivateDelegate>(mgr, 3)(
                mgr, TsfProfile.TfProfileTypeKeyboardLayout, 0x0409, ref empty, ref empty, hkl,
                TsfProfile.TfIppmForProcess);
            fields["activate_hresult"] = $"0x{hrAct:X8}";
            if (hrAct != 0)
            {
                return false;
            }
            var hrVer = Method<ProfileGetActiveDelegate>(mgr, 10)(mgr, ref cat, out var now);
            var ok = hrVer >= 0
                && now.ProfileType == TsfProfile.TfProfileTypeKeyboardLayout
                && now.LangId == 0x0409
                && (now.Hkl == hkl || now.SubstituteHkl == hkl);
            fields["active_read_hresult"] = $"0x{hrVer:X8}";
            fields["verified_active_english"] = ok;
            fields["active_type"] = now.ProfileTypeName;
            fields["active_langid"] = $"0x{now.LangId:X4}";
            fields["active_hkl"] = $"0x{now.Hkl.ToInt64():X}";
            return ok;
        }
        finally
        {
            Marshal.Release(mgr);
        }
    }

    private static (IntPtr Hkl, TsfProfile.InputProcessorProfile Profile)? _englishRestore;


    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    // --- ITfInputProcessorProfileMgr（msctf）键盘布局 profile 查询/激活——仅 --english-keyboard 使用。 ---
    // 接口布局：IUnknown(0-2) 后 ActivateProfile(3)/DeactivateProfile(4)/GetProfile(5)/EnumProfiles(6)/
    // ReleaseInputProcessor(7)/RegisterProfile(8)/UnregisterProfile(9)/GetActiveProfile(10)。
    private delegate int ProfileActivateDelegate(
        IntPtr self, uint profileType, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);
    private delegate int ProfileGetDelegate(
        IntPtr self, uint profileType, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl,
        out TsfProfile.InputProcessorProfile result);
    private delegate int ProfileGetActiveDelegate(IntPtr self, ref Guid category, out TsfProfile.InputProcessorProfile result);

    private static T Method<T>(IntPtr manager, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager), slot * IntPtr.Size));

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);


    // --- window-deactivate：自有顶层辅助窗口（UI 线程 CreateWindowEx/ShowWindow/
    //     SetActiveWindow——前台/活动状态按拥有者线程输入队列归属，同进程同队列合法转移）。 ---
    private delegate IntPtr AuxWndProcDelegate(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    private static readonly AuxWndProcDelegate _auxWndProc = AuxWindowProc;
    private static IntPtr _auxHwnd = IntPtr.Zero;
    private static string? _auxClassName;
    private static IntPtr AuxWindowProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam)
        => DefWindowProc(hwnd, msg, wparam, lparam);

    /// <summary>UI 线程执行：创建顶层重叠辅助窗并 ShowWindow+SetActiveWindow 自然激活，
    /// 返回完整诊断（线程/进程归属、style/exstyle、active/focus/foreground、调用 tid）。
    /// created=false 时无窗口可用；created=true 但未夺前台如实记录，不假装成功。</summary>
    private static Dictionary<string, object?> AuxActivate(IntPtr hostHwnd)
    {
        var info = new Dictionary<string, object?>
        {
            ["call_managed_tid"] = Environment.CurrentManagedThreadId,
            ["call_win32_tid"] = NativeMethods.CurrentWin32ThreadId(),
        };
        // 宿主窗口归属诊断（应与本调用同线程/同输入队列）。
        var hostTid = NativeMethods.GetWindowThreadProcessId(hostHwnd, out var hostPid);
        info["host_hwnd"] = $"0x{hostHwnd.ToInt64():X}";
        info["host_tid"] = hostTid;
        info["host_pid"] = hostPid;
        info["host_style"] = $"0x{(long)WindowLong(hostHwnd, -16):X}"; // GWL_STYLE
        info["host_exstyle"] = $"0x{(long)WindowLong(hostHwnd, -20):X}"; // GWL_EXSTYLE
        info["host_owner"] = $"0x{(long)WindowLong(hostHwnd, -8):X}"; // GWLP_HWNDPARENT
        info["foreground_before"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}";
        info["active_before"] = $"0x{NativeMethods.GetActiveWindow().ToInt64():X}";
        info["focus_before"] = $"0x{NativeMethods.GetFocus().ToInt64():X}";

        _auxClassName = "ImeHostCheckAux-" + Guid.NewGuid().ToString("N");
        var wc = new AuxWndClass
        {
            cbSize = (uint)Marshal.SizeOf<AuxWndClass>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_auxWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _auxClassName,
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            info["created"] = false;
            info["register_error"] = Marshal.GetLastWin32Error();
            return info;
        }

        // WS_OVERLAPPED | WS_VISIBLE，无 WS_CHILD/NOACTIVATE；普通顶层自有窗口。
        _auxHwnd = CreateWindowEx(
            0, _auxClassName, "ImeHostCheckAux", 0x00000000u | 0x10000000u /* WS_OVERLAPPED|WS_VISIBLE */,
            -32000, -32000, 10, 10,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        info["created"] = _auxHwnd != IntPtr.Zero;
        info["create_error"] = _auxHwnd == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (_auxHwnd == IntPtr.Zero)
        {
            return info;
        }

        var auxTid = NativeMethods.GetWindowThreadProcessId(_auxHwnd, out var auxPid);
        info["aux_hwnd"] = $"0x{_auxHwnd.ToInt64():X}";
        info["aux_tid"] = auxTid;
        info["aux_pid"] = auxPid;
        info["aux_style"] = $"0x{(long)WindowLong(_auxHwnd, -16):X}";
        info["aux_exstyle"] = $"0x{(long)WindowLong(_auxHwnd, -20):X}";
        info["aux_owner"] = $"0x{(long)WindowLong(_auxHwnd, -8):X}";
        info["same_input_queue"] = auxTid == hostTid;

        NativeMethods.ShowWindow(_auxHwnd, 1); // SW_SHOWNORMAL
        var prior = SetActiveWindow(_auxHwnd);
        info["set_active_result"] = $"0x{prior.ToInt64():X}";
        info["active_after"] = $"0x{NativeMethods.GetActiveWindow().ToInt64():X}";
        info["focus_after"] = $"0x{NativeMethods.GetFocus().ToInt64():X}";
        info["foreground_after"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}";
        return info;
    }


    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr WindowLong(IntPtr hwnd, int index);

    /// <summary>UI 线程执行：销毁辅助窗并置零（幂等）。</summary>
    private static void AuxDestroy()
    {
        if (_auxHwnd != IntPtr.Zero)
        {
            DestroyWindow(_auxHwnd);
            _auxHwnd = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AuxWndClass
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", ExactSpelling = true)]
    private static extern ushort RegisterClassEx(ref AuxWndClass wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", ExactSpelling = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", ExactSpelling = true)]
    private static extern IntPtr GetModuleHandle(string? name);


}

/// <summary>组合宿主场景的真实视图 harness：以 Component + UseState 提供
/// fieldDisabled/fieldMounted 真实切换——禁用走 .Disabled() 修饰（真实
/// IsEffectivelyDisabled 路径），卸载走条件视图替换（真实节点移除）。状态值由
/// Build 期捕获到实例字段；脚本线程经 UI 线程 Query 写 State.Value。</summary>
internal sealed class HarnessComponent : Component
{
    private readonly TextField _field;
    private readonly TextField _field2;
    private readonly bool _useTsf;
    private readonly Action<string> _layoutMode;

    /// <summary>组合中真实禁用开关；由 UI 线程写。</summary>
    internal State<bool>? FieldDisabled;
    /// <summary>组合中真实卸载开关；由 UI 线程写。</summary>
    internal State<bool>? FieldMounted;

    internal HarnessComponent(TextField field, TextField field2, bool useTsf, Action<string> layoutMode)
    {
        _field = field;
        _field2 = field2;
        _useTsf = useTsf;
        _layoutMode = layoutMode;
    }

    protected override View Build()
    {
        var disabled = UseState("fieldDisabled", false);
        var mounted = UseState("fieldMounted", true);
        FieldDisabled = disabled;
        FieldMounted = mounted;

        return new Responsive(info =>
        {
            var narrow = info.AvailableWidth < 460;
            View f1 = mounted.Value
                ? (disabled.Value ? _field.Disabled(true) : _field)
                : new Spacer(1);
            View fields = narrow
                ? new VStack(f1, new Spacer(120), _field2) { Spacing = 8 }
                : new HStack(f1, _field2) { Spacing = 8 };
            _layoutMode(narrow ? "narrow-vstack" : "wide-hstack");
            // 固定高尾部保证两种模式下内容都高过视口，垂直滚动始终可达。
            return new VStack(fields, new Spacer(_useTsf ? 900 : 320)) { Spacing = 8 }.Padding(8);
        });
    }
}
