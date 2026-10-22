using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 真实隐藏窗口 + 真实 TSF 线程管理器/文档管理器/上下文 smoke：
/// 注册/创建隐藏窗口（绝不 ShowWindow），在独立线程上分别以 MTA/STA 接入 TSF，
/// 验证 CreateContext、store sink advice、owner CCW QI、Push/Associate/SetFocus、GetFocus、
/// SetFocus(NULL) 的真实 HRESULT 与清理资源释放。不显示窗口、不注入键盘、不启动可见主程序。
/// </summary>
internal static class NativeSmoke
{
    internal static int Run(bool? staFilter, string outDir, bool uiElementEnabledOnly = false)
    {
        Directory.CreateDirectory(outDir);
        using var log = new ProbeLog(Path.Combine(outDir, "native-selftest.jsonl"), echo: false);
        var traceSink = new ProbeTsfTrace(log);
        var apartments = staFilter is null ? new[] { false, true } : new[] { staFilter.Value };
        var failed = 0;

        foreach (var sta in apartments)
        {
            var result = RunApartment(sta, log, traceSink.Write, uiElementEnabledOnly);
            if (!result.Pass)
            {
                failed++;
            }
        }

        Console.WriteLine(
            $"native-selftest: {(failed == 0 ? "PASS" : "FAIL")} ({apartments.Length - failed}/{apartments.Length} apartments)");
        return failed == 0 ? 0 : 1;
    }

    private static SmokeResult RunApartment(bool sta, ProbeLog log, TsfTrace trace,
        bool uiElementEnabledOnly = false)
    {
        var result = new SmokeResult(sta ? "STA" : "MTA");
        var thread = new Thread(() => RunOnThread(sta, log, trace, result, uiElementEnabledOnly))
        {
            IsBackground = false,
            Name = sta ? "tsf-native-smoke-sta" : "tsf-native-smoke-mta",
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(sta ? ApartmentState.STA : ApartmentState.MTA);
        }

        thread.Start();
        thread.Join();
        return result;
    }

    private static void RunOnThread(bool sta, ProbeLog log, TsfTrace trace, SmokeResult result,
        bool uiElementEnabledOnly = false)
    {
        var events = new List<(string Stage, string Kind, IDictionary<string, object?>? Fields)>();
        TsfTrace recorded = (stage, kind, fields) =>
        {
            lock (events)
            {
                events.Add((stage, kind, fields));
            }

            trace(stage, kind, fields);
        };

        WindowNative.WndProcDelegate proc = (hWnd, msg, wParam, lParam) =>
            WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        var className = $"CSharp.UI.TsfHostCheck.NativeSmoke.{Environment.ProcessId}.{result.Requested}";
        var instance = WindowNative.GetModuleHandleW(null);
        var wc = new WndClassW
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(proc),
            Instance = instance,
            ClassName = className,
        };

        var atom = WindowNative.RegisterClassW(ref wc);
        result.Check("register-class", atom != 0, $"atom={atom} class={className}");
        if (atom == 0)
        {
            return;
        }

        var hwnd = WindowNative.CreateWindowExW(
            0, className, "CSharp.UI TSF native smoke (hidden)", 0,
            0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        result.Check("create-hidden-window", hwnd != IntPtr.Zero,
            $"hwnd=0x{hwnd.ToInt64():X} visible={NativeMethods.IsWindowVisible(hwnd)}");
        if (hwnd == IntPtr.Zero)
        {
            WindowNative.UnregisterClassW(className, instance);
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        result.Check("dpi-awareness-per-monitor-v2", DpiNative.IsPerMonitorV2(),
            DpiNative.DescribeThreadAwareness());
        result.Check("window-dpi", dpi > 0,
            $"dpi={dpi} process={DpiNative.DescribeProcessAwareness()}");

        var store = new AcpTextStore(recorded);
        var pending = new ConcurrentQueue<Action>();
        store.NotificationDispatcher = pending.Enqueue;
        var session = new TsfSessionHost(store, hwnd, log, recorded, sta, () => { },
            uiElementEnabledOnly: uiElementEnabledOnly);

        try
        {
            var engageHr = session.Engage();
            result.Check("engage", engageHr >= 0, TsfHResult.Describe(engageHr));
            result.Check("actual-apartment", session.Apartment.Contains("type=", StringComparison.Ordinal),
                session.Apartment);

            if (engageHr >= 0)
            {
                result.Check("store-sink-advised", store.HasSink,
                    $"mask=0x{store.SinkMask:X}");
                result.Check("owner-ccw-itextstoreacp",
                    QueryInterfacePresent(store, TsfIids.TextStoreAcp));
                result.Check("owner-ccw-composition-sink",
                    QueryInterfacePresent(store, TsfIids.ContextOwnerCompositionSink));

                var state = session.ResourceState;
                result.Check("activate/push/associate/focus/uielement",
                    state.Activated && state.ContextPushed && state.Associated
                    && state.FocusSet && state.UiAdvised,
                    state.ToString());

                // R26：激活方式可辨；显式开启时记录真实 active flags，须含 UIElement-only 位。
                result.Check("activation-mode", session.ActivationMode is "activate" or "activate-ex",
                    $"mode={session.ActivationMode} uielement_enabled_only={session.UiElementEnabledOnly}");
                if (uiElementEnabledOnly)
                {
                    var activeFlags = session.ReadActiveActivationFlags("native-selftest");
                    result.Check("uielement-enabled-only-active",
                        activeFlags is { } value
                        && (value & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) != 0,
                        $"flags={(activeFlags is { } f ? $"0x{f:X}" : "(unavailable)")} "
                        + $"mode={session.ActivationMode}");
                }

                var threadMgr = session.ThreadManager;
                var documentMgr = session.DocumentManager;
                if (threadMgr is not null && documentMgr is not null)
                {
                    var focusHr = threadMgr.GetFocus(out var focus);
                    var sameAsDocument = focus is not null && SameComIdentity(focus, documentMgr);
                    if (focus is not null)
                    {
                        Marshal.ReleaseComObject(focus);
                    }

                    result.Check("get-focus", focusHr >= 0 && sameAsDocument,
                        $"{TsfHResult.Describe(focusHr)} same_as_document={sameAsDocument}");

                    var threadFocusHr = threadMgr.IsThreadFocus(out var threadFocus);
                    result.Check("is-thread-focus", threadFocusHr >= 0,
                        $"{TsfHResult.Describe(threadFocusHr)} focus={threadFocus}");
                }
                else
                {
                    result.Check("thread/document-manager", false, "null after engage");
                }

                PumpMessages(store, pending, 200);
                result.Check("message-pump", true, "200ms");

                // 建立真实 view layout，使清理阶段能发送 TS_LC_DESTROY。
                store.PublishLayout(AcpLayoutSnapshot.Build(
                    store.LayoutGeneration,
                    store.Document.Version,
                    store.Document.Text,
                    Array.Empty<int>(),
                    20,
                    TsfRect.FromLtrb(0, 0, 100, 50),
                    0,
                    0,
                    0,
                    0), notify: true);

                // R24 真实接线：GetScreenExt 不要求读锁；未显示窗口返回 S_OK + 零矩形，
                // 而非回退到上面发布的布局矩形（表面独立于文本布局）。
                // 显式挂接真实 HWND，走 FromWindow 原生查询路径。
                store.AttachWindow(hwnd);
                var screenHr = store.GetScreenExt(1, out var screenRect);
                result.Check("screen-ext-no-lock-hidden-window",
                    screenHr == TsfHResult.S_OK && screenRect.IsEmpty,
                    $"{TsfHResult.Describe(screenHr)} rect={screenRect} hwnd=0x{hwnd.ToInt64():X}");
            }
        }
        catch (Exception ex)
        {
            result.Check("native-smoke-exception", false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            session.Cleanup();
            result.Check("cleanup", session.CleanupFailures == 0,
                $"failures={session.CleanupFailures}");

            var setFocusStep = session.LastCleanupSteps
                .FirstOrDefault(step => step.Name == "set-focus-null");
            result.Check("set-focus-null-hresult", setFocusStep.Name is not null,
                setFocusStep.Name is null
                    ? "step missing"
                    : TsfHResult.Describe(setFocusStep.HResult));

            var after = session.ResourceState;
            result.Check("resources-released",
                !after.ComInitialized && !after.HasThreadManager
                && !after.HasDocumentManager && !after.HasContext,
                after.ToString());
            result.Check("composition-cleared",
                !store.IsComposing && store.CompositionObserver is null);

            var destroyIndex = events.FindIndex(evt =>
                evt.Stage == "notify" && evt.Kind == "layout"
                && evt.Fields is { } fields
                && fields.TryGetValue("layout_code", out var code)
                && code is int layoutCode && layoutCode == 2);
            var unadviseIndex = events.FindIndex(evt =>
                evt.Stage == "store" && evt.Kind == "unadvise");
            var popIndex = events.FindIndex(evt =>
                evt.Stage == "cleanup" && evt.Kind == "pop-context");
            result.Check("layout-destroy-before-teardown",
                destroyIndex >= 0 && popIndex > destroyIndex
                && (unadviseIndex < 0 || unadviseIndex > destroyIndex),
                $"destroy={destroyIndex} unadvise={unadviseIndex} pop={popIndex}");

            WindowNative.DestroyWindow(hwnd);
            result.Check("destroy-window", true, null);
            var unregistered = WindowNative.UnregisterClassW(className, instance);
            result.Check("unregister-class", unregistered, $"ok={unregistered}");
        }

        GC.KeepAlive(proc);
    }

    private static void PumpMessages(AcpTextStore store, ConcurrentQueue<Action> pending, int durationMs)
    {
        var deadline = Environment.TickCount64 + durationMs;
        while (Environment.TickCount64 < deadline)
        {
            while (WindowNative.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, WindowNative.PM_REMOVE))
            {
                WindowNative.TranslateMessage(ref msg);
                WindowNative.DispatchMessageW(ref msg);
            }

            while (pending.TryDequeue(out var action))
            {
                action();
            }

            Thread.Sleep(10);
        }

        store.FlushDeferredNotifications();
    }

    private static bool QueryInterfacePresent(object instance, Guid iid)
    {
        IntPtr unknown = IntPtr.Zero;
        IntPtr iface = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(instance);
            var interfaceId = iid;
            var hr = Marshal.QueryInterface(unknown, in interfaceId, out iface);
            return hr == 0 && iface != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (iface != IntPtr.Zero)
            {
                Marshal.Release(iface);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    private static bool SameComIdentity(object left, object right)
    {
        IntPtr leftUnknown = IntPtr.Zero;
        IntPtr rightUnknown = IntPtr.Zero;
        try
        {
            leftUnknown = Marshal.GetIUnknownForObject(left);
            rightUnknown = Marshal.GetIUnknownForObject(right);
            return leftUnknown == rightUnknown;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (leftUnknown != IntPtr.Zero)
            {
                Marshal.Release(leftUnknown);
            }

            if (rightUnknown != IntPtr.Zero)
            {
                Marshal.Release(rightUnknown);
            }
        }
    }

    private sealed class SmokeResult
    {
        private readonly List<(string Name, bool Pass, string? Detail)> _steps = new();

        internal SmokeResult(string requested) => Requested = requested;

        internal string Requested { get; }

        internal bool Pass => _steps.Count > 0 && _steps.All(step => step.Pass);

        internal void Check(string name, bool pass, string? detail = null)
        {
            _steps.Add((name, pass, detail));
            Console.WriteLine(
                $"[native-smoke {Requested}] {(pass ? "PASS" : "FAIL")} {name}"
                + (string.IsNullOrEmpty(detail) ? string.Empty : $" — {detail}"));
        }
    }
}
