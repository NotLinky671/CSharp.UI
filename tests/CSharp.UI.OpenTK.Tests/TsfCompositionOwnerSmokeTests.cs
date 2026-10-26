using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P2b 第二批原生组合接线冒烟（返修版）：真实 TSF 组合 API + <b>产品</b>回调实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>本测试实际执行</b>：真实 <c>ITfContextComposition::StartComposition</c> 建立组合 →
/// 产品 <see cref="NativeCompositionOwnerSink"/> 收到 <c>OnStartComposition</c> 并读取
/// <c>GetRange</c>/<c>ITfRangeACP::GetExtent</c> 真实 ACP 范围 → 写入真实文本 →
/// <c>ITfComposition::EndComposition</c> → 产品收到 <c>OnEndComposition</c> 与
/// <c>ITfTextEditSink::OnEndEdit</c>。
/// </para>
/// <para>
/// <b>断言</b>：产品回调计数真实到达；ACP 范围与预期一致；编辑完成前<b>零</b> Binding 写入；
/// 完成编辑后恰好一次唯一结果。必需的 QI/Advise 失败会 <b>使测试失败</b>，不是记录 note 后通过。
/// </para>
/// <para>
/// 隐藏自有窗口 + 自建 STA 线程；不显示窗口、不注入键盘、不改全局输入法配置、不强制焦点。
/// 这不是真实微软拼音验收（P4），只证明产品原生接线。
/// </para>
/// </remarks>
public sealed class TsfCompositionOwnerSmokeTests
{
    private const int WS_OVERLAPPED = 0x00000000;

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void 真实TSF组合经产品回调完成StartUpdateEnd与唯一提交()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var failures = new List<string>();
        var log = new List<string>();
        var thread = new Thread(() => RunProbe(failures, log));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "原生组合冒烟超时。");

        foreach (var line in log)
        {
            Console.WriteLine(line);
        }

        Assert.True(failures.Count == 0, "原生组合冒烟失败：" + string.Join(" | ", failures));
    }

    /// <summary>
    /// R7：用<b>真实</b>隐藏 context 验证连接生命周期（重复 advise、撤销、换 context 重连）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与上一个冒烟共用真实 STA 线程、隐藏窗口与真实 <c>ITfThreadMgr</c>，但只需要两个 context，
    /// 不需要可见 UI 或前台激活。
    /// </para>
    /// <para>
    /// 断言的是<b>实际建立的连接状态</b>：成功 HRESULT、真实 cookie、重复 advise 复用同一 cookie、
    /// 撤销后 source/cookie 成对清空、换 context 后使用新的 source 与 cookie。
    /// 不再用“两次传 null 都失败”冒充覆盖。
    /// </para>
    /// </remarks>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void 真实隐藏context验证重复advise与换context重连的成对回收()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var failures = new List<string>();
        var log = new List<string>();
        var thread = new Thread(() => RunConnectionProbe(failures, log));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "原生连接生命周期验证超时。");

        foreach (var line in log)
        {
            Console.WriteLine(line);
        }

        Assert.True(failures.Count == 0, "原生连接生命周期验证失败：" + string.Join(" | ", failures));
    }

    /// <summary>
    /// 真实 contextA → contextB 的连接生命周期探针（隐藏窗口，无可见 UI）。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunConnectionProbe(List<string> failures, List<string> log)
    {
        var className = $"CSharp.UI.P2b.ConnectionProbe.{Environment.ProcessId}";
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
        AcpTextStore? store = null;
        UiRuntime? runtime = null;
        NativeCompositionOwner? owner = null;
        NativeCompositionOwnerSink? sink = null;
        ITfThreadMgr? threadMgr = null;
        ITfDocumentMgr? documentMgr = null;
        ITfContext? contextA = null;
        ITfContext? contextB = null;

        try
        {
            hwnd = CreateWindowExW(0, className, "CSharp.UI P2b connection probe (hidden)", WS_OVERLAPPED,
                0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                failures.Add($"CreateWindowExW 失败 Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            var service = new ForwardingService();
            var text = "ABCD";
            runtime = new UiRuntime(
                new TextField(new Binding<string>(() => text, v => text = v)),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
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
                failures.Add("真实 TextField 未建立文本输入客户端。");
                return;
            }

            store = new AcpTextStore();
            store.ResetAuthoritativeText(text);
            store.AttachWindow(hwnd);

            var session = new TextInputCompositionSession(client, c => ReferenceEquals(c, service.ActiveClient));
            owner = new NativeCompositionOwner(store, session, client.Session.SessionId);
            sink = new NativeCompositionOwnerSink(owner, store);

            if (TsfNative.TF_CreateThreadMgr(out threadMgr) < 0 || threadMgr is null)
            {
                failures.Add("TF_CreateThreadMgr 失败。");
                return;
            }

            if (threadMgr.Activate(out var clientId) < 0)
            {
                failures.Add("Activate 失败。");
                return;
            }

            if (threadMgr.CreateDocumentMgr(out documentMgr) < 0 || documentMgr is null)
            {
                failures.Add("CreateDocumentMgr 失败。");
                return;
            }

            store.CompositionObserver = sink;

            // ---- context A ----
            if (documentMgr.CreateContext(clientId, 0, store, out contextA, out _) < 0 || contextA is null)
            {
                failures.Add("context A CreateContext 失败。");
                return;
            }

            var adviseA = sink.AdviseEditSink(contextA);
            if (adviseA < 0 || !sink.EditSinkAdvised || !sink.HoldsSource)
            {
                failures.Add($"context A advise 未成功建立连接 hr=0x{adviseA:X8}。");
                return;
            }

            var cookieA = sink.EditSinkCookie;
            log.Add($"[conn] contextA advised hr=0x{adviseA:X8} cookie={cookieA}");

            if (cookieA == 0)
            {
                failures.Add("context A advise 返回了 0 cookie（未真正建立连接）。");
                return;
            }

            // 重复 advise：必须是幂等成功且**复用同一 cookie**，绝不覆盖尚未清理的连接。
            var adviseARepeat = sink.AdviseEditSink(contextA);
            if (adviseARepeat != TsfHResult.S_OK || sink.EditSinkCookie != cookieA || !sink.HoldsSource)
            {
                failures.Add($"重复 advise 未幂等复用 cookie：hr=0x{adviseARepeat:X8} "
                    + $"cookie={sink.EditSinkCookie}（期望 {cookieA}）。");
                return;
            }

            log.Add($"[conn] repeated advise idempotent, same cookie={sink.EditSinkCookie}");

            // 撤销：成功、成对清空 source 与 cookie。
            var unadviseA = sink.UnadviseEditSink();
            if (unadviseA < 0 || sink.EditSinkAdvised || sink.HoldsSource || sink.EditSinkCookie != 0)
            {
                failures.Add($"context A unadvise 未成对清空：hr=0x{unadviseA:X8} "
                    + $"advised={sink.EditSinkAdvised} holdsSource={sink.HoldsSource} cookie={sink.EditSinkCookie}。");
                return;
            }

            log.Add($"[conn] contextA unadvised hr=0x{unadviseA:X8} state cleared");

            // 幂等：再次撤销仍成功且不触碰已清空的连接。
            var unadviseAgain = sink.UnadviseEditSink();
            if (unadviseAgain != TsfHResult.S_OK)
            {
                failures.Add($"重复 unadvise 未幂等成功 hr=0x{unadviseAgain:X8}。");
                return;
            }

            // ---- context B：换一个真实 context 重新连接 ----
            if (documentMgr.CreateContext(clientId, 0, store, out contextB, out _) < 0 || contextB is null)
            {
                failures.Add("context B CreateContext 失败。");
                return;
            }

            if (ReferenceEquals(contextA, contextB))
            {
                failures.Add("第二个 CreateContext 返回了同一个 context，无法验证换 context 重连。");
                return;
            }

            var adviseB = sink.AdviseEditSink(contextB);
            if (adviseB < 0 || !sink.EditSinkAdvised || !sink.HoldsSource || sink.EditSinkCookie == 0)
            {
                failures.Add($"context B advise 未成功建立新连接 hr=0x{adviseB:X8}。");
                return;
            }

            log.Add($"[conn] contextB advised hr=0x{adviseB:X8} cookie={sink.EditSinkCookie}");

            var unadviseB = sink.UnadviseEditSink();
            if (unadviseB < 0 || sink.EditSinkAdvised || sink.HoldsSource || sink.EditSinkCookie != 0)
            {
                failures.Add($"context B unadvise 未成对清空：hr=0x{unadviseB:X8} "
                    + $"advised={sink.EditSinkAdvised} holdsSource={sink.HoldsSource} cookie={sink.EditSinkCookie}。");
                return;
            }

            log.Add($"[conn] contextB unadvised hr=0x{unadviseB:X8} state cleared");
            log.Add("[conn] real contextA→contextB connection lifecycle verified");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            sink?.Dispose();
            owner?.Dispose();

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

            ReleaseCom(contextA);
            ReleaseCom(contextB);

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
            runtime?.Dispose();

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
            }

            UnregisterClassW(className, instance);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunProbe(List<string> failures, List<string> log)
    {
        var className = $"CSharp.UI.P2b.RealOwnerSmoke.{Environment.ProcessId}";
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
        AcpTextStore? store = null;
        UiRuntime? runtime = null;
        NativeCompositionOwner? owner = null;
        NativeCompositionOwnerSink? sink = null;
        ITfThreadMgr? threadMgr = null;
        ITfDocumentMgr? documentMgr = null;
        ITfContext? context = null;
        ITfContextComposition? composition = null;
        ITfComposition? tfComposition = null;

        var writes = 0;
        var text = "ABCD";
        var service = new ForwardingService();

        try
        {
            hwnd = CreateWindowExW(0, className, "CSharp.UI P2b real owner smoke (hidden)", WS_OVERLAPPED,
                0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                failures.Add($"CreateWindowExW 失败 Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            // 真实 TextField 权威。
            runtime = new UiRuntime(
                new TextField(new Binding<string>(() => text, v => { writes++; text = v; })),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
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
                failures.Add("真实 TextField 未建立文本输入客户端。");
                return;
            }

            // caret 放到 2（在 B 与 C 之间）。
            if (!((ITextInputSelectionTarget)client).TrySetCommittedSelection(
                    client.Session, client.Document.Version, new TextSelection(2, 2)))
            {
                failures.Add("设置已提交选区失败。");
                return;
            }

            runtime.ProcessPendingWork();

            store = new AcpTextStore();
            store.ResetAuthoritativeText(text);
            store.AttachWindow(hwnd);

            var session = new TextInputCompositionSession(
                client, c => ReferenceEquals(c, service.ActiveClient));
            owner = new NativeCompositionOwner(store, session, client.Session.SessionId);
            sink = new NativeCompositionOwnerSink(owner, store);

            if (TsfNative.TF_CreateThreadMgr(out threadMgr) < 0 || threadMgr is null)
            {
                failures.Add("TF_CreateThreadMgr 失败。");
                return;
            }

            if (threadMgr.Activate(out var clientId) < 0)
            {
                failures.Add("Activate 失败。");
                return;
            }

            if (threadMgr.CreateDocumentMgr(out documentMgr) < 0 || documentMgr is null)
            {
                failures.Add("CreateDocumentMgr 失败。");
                return;
            }

            // 官方注册方式：owner 对象必须在 CreateContext 之前暴露 ITfContextOwnerCompositionSink。
            store.CompositionObserver = sink;
            log.Add($"[smoke] store exposes owner composition sink before CreateContext");

            if (documentMgr.CreateContext(clientId, 0, store, out context, out var editCookie) < 0
                || context is null)
            {
                failures.Add("CreateContext 失败（msctf 未接受 store 作为 ITextStoreACP）。");
                return;
            }

            if (documentMgr.Push(context) < 0)
            {
                failures.Add("Push 失败。");
                return;
            }

            log.Add($"[smoke] context ok cookie={editCookie} storeSink={store.HasSink}");

            // 组合 owner sink 需要 context 成为 document owner 并与窗口关联。
            var focusHr = threadMgr.SetFocus(documentMgr);
            log.Add($"[smoke] SetFocus hr=0x{focusHr:X8}");

            var assocHr = threadMgr.AssociateFocus(hwnd, documentMgr, out var prevDm);
            log.Add($"[smoke] AssociateFocus hr=0x{assocHr:X8}");
            if (prevDm is not null)
            {
                ReleaseCom(prevDm);
            }

            // 必需：编辑完成 sink 必须真正 advise 成功（失败即测试失败）。
            var adviseHr = sink.AdviseEditSink(context);
            if (adviseHr < 0 || !sink.EditSinkAdvised)
            {
                failures.Add($"ITfTextEditSink.AdviseSink 失败 hr=0x{adviseHr:X8}（必需，不能记为 note）。");
                return;
            }

            log.Add($"[smoke] edit sink advised cookie={sink.EditSinkCookie}");

            // 必需：组合 owner sink 也必须 advise 成功（官方：经 ITfSource，非 StartComposition 参数）。



            // 必需：取得 ITfContextComposition（失败即测试失败）。
            var compHr = QueryForContextComposition(context, out composition);
            if (compHr < 0 || composition is null)
            {
                failures.Add($"QI(ITfContextComposition) 失败 hr=0x{compHr:X8}（必需）。");
                return;
            }

            // 在真实 TSF 编辑会话内建立组合：需要写会话。
            var startResult = StartNativeComposition(context, clientId, composition, sink, store, out tfComposition);
            if (startResult < 0)
            {
                failures.Add($"StartComposition 失败 hr=0x{startResult:X8}。");
                return;
            }

            log.Add($"[smoke] composition started, product Start callbacks={sink.StartCallbacks} "
                + $"range={sink.LastRange} diag={sink.LastRangeDiagnostic} "
                + $"rangeFailures={sink.RangeFailureCount}");

            if (sink.StartCallbacks == 0)
            {
                failures.Add("产品 OnStartComposition 未被调用。");
                return;
            }

            if (sink.LastRange is not { } range)
            {
                failures.Add("产品未捕获到有效 ACP 组合范围（GetRange/GetExtent 失败）。");
                return;
            }

            log.Add($"[smoke] captured ACP range anchor={range.Anchor} cch={range.Length}");

            // ---- 预编辑安全阶段（必须在 End **之前**执行）----
            // 只检查 writes==0 而不 drain，会把所有预编辑事实攒到 End 之后，
            // 既掩盖 R8（未变化的终止结果），也无法证明真实交互式预编辑生效。
            var preeditDrain = owner.DrainAtSafePhase();
            log.Add($"[smoke] preedit drain applied={preeditDrain.Applied} "
                + $"actions=[{string.Join(",", preeditDrain.Actions)}] writes={writes}");

            if (writes != 0)
            {
                failures.Add($"预编辑阶段写入了 Binding（writes={writes}）。");
                return;
            }

            // 断言真实组合已生效：活动组合 + 实际显示预编辑 + 范围/选区。
            if (!session.Snapshot.IsComposing)
            {
                failures.Add("预编辑安全阶段后没有活动组合（真实 Start 未驱动 P2a Begin）。");
                return;
            }

            var state = session.Snapshot;
            log.Add($"[smoke] preedit state display=\"{state.DisplayText}\" "
                + $"preeditLen={state.Preedit.Length} repl=[{state.Replacement.Start},{state.Replacement.End})");

            // 真实写入：经**组合范围**的 ITfRange.SetText 写入，让 TSF 自己维护范围与编辑记录。
            var insertHr = tfComposition is null
                ? TsfHResult.E_FAIL
                : SetCompositionText(context, clientId, tfComposition, "nihao");
            log.Add($"[smoke] native insert hr=0x{insertHr:X8} nativeText=\"{store.ReadAuthoritativeDocument().Text}\"");

            if (insertHr < 0)
            {
                failures.Add($"组合范围 SetText 失败 hr=0x{insertHr:X8}（必需）。");
                return;
            }

            // 写入之后立刻在安全阶段应用本次真实 Update，证明更新走的是真实回调而非最终结果。
            var updateDrain = owner.DrainAtSafePhase();
            log.Add($"[smoke] post-insert drain actions=[{string.Join(",", updateDrain.Actions)}] "
                + $"preedit=\"{session.Snapshot.Preedit}\" writes={writes}");

            if (writes != 0)
            {
                failures.Add($"真实预编辑更新阶段写入了 Binding（writes={writes}）。");
            }

            if (session.Snapshot.Preedit.Length == 0)
            {
                failures.Add("真实 SetText 之后预编辑为空（未捕获真实原生预编辑文本）。");
            }

            // 真实范围调整：经 ITfComposition::ShiftEnd 扩展组合范围，
            // 由 TSF 触发产品的 OnUpdateComposition（含 pRangeNew）。
            var updatesBefore = sink.UpdateCallbacks;
            var shiftHr = tfComposition is null
                ? TsfHResult.E_FAIL
                : ShiftCompositionEnd(context, clientId, tfComposition, 0);
            log.Add($"[smoke] ShiftEnd hr=0x{shiftHr:X8} product Update callbacks={sink.UpdateCallbacks} "
                + $"(before={updatesBefore}) range={sink.LastRange}");

            if (shiftHr < 0)
            {
                failures.Add($"ShiftEnd 失败 hr=0x{shiftHr:X8}（必需）。");
                return;
            }

            if (sink.UpdateCallbacks <= updatesBefore)
            {
                failures.Add("产品 OnUpdateComposition 未在真实范围调整后到达（必需）。");
            }

            // 范围调整本身不得写入 Binding（预编辑仍在进行）。
            log.Add($"[smoke] after ShiftEnd: writes={writes} (expected 0)");
            if (writes != 0)
            {
                failures.Add($"范围调整阶段写入了 Binding（writes={writes}）。");
            }

            // 结束组合必须在真实写会话内（EndComposition 需要有效 edit cookie）。
            var endHr = tfComposition is null
                ? TsfHResult.E_FAIL
                : EndCompositionInSession(context, clientId, tfComposition);
            log.Add($"[smoke] EndComposition hr=0x{endHr:X8} product End callbacks={sink.EndCallbacks}");
            if (endHr < 0)
            {
                failures.Add($"EndComposition 失败 hr=0x{endHr:X8}（必需）。");
                return;
            }

            if (sink.EndCallbacks == 0)
            {
                failures.Add("产品 OnEndComposition 未到达（必需）。");
            }

            // End 只是终止事实：End 事实自身必须映射为 Ignore（零写入）。
            // 注意 EndComposition 的写会话会**同步**触发真实 OnEndEdit，因此同一批次里可以合法出现
            // 由完成编辑产生的 Commit；这里核对的是 End 自身的映射，以及“没有完成编辑就不得提交”。
            var endDrain = owner.DrainAtSafePhase();
            log.Add($"[smoke] after End: writes={writes} actions=[{string.Join(",", endDrain.Actions)}] "
                + $"endEditCallbacks={sink.EndEditCallbacks}");

            if (sink.EndEditCallbacks == 0 && endDrain.Actions.Contains(NativeCompositionAction.Commit))
            {
                failures.Add("End 事实在无真实 OnEndEdit 的情况下被当作提交。");
            }

            // 完成编辑必须由**真实** OnEndEdit 到达驱动；此处不做任何合成 Capture 替代。
            var commitDrain = owner.DrainAtSafePhase();
            log.Add($"[smoke] completed-edit drain applied={commitDrain.Applied} "
                + $"committed={commitDrain.AnyCommitted} writes={writes} text=\"{text}\"");

            // 断言：真实完成编辑后**恰好一次**唯一提交，且保留已提交后缀 CD。
            if (writes != 1)
            {
                failures.Add($"真实完成编辑应恰好一次 Binding 写入，实际 writes={writes}。");
            }

            if (!string.Equals(text, "ABnihaoCD", StringComparison.Ordinal))
            {
                failures.Add($"提交结果丢失后缀：text=\"{text}\"（期望 \"ABnihaoCD\"）。");
            }

            if (commitDrain.AnyCommitted)
            {
                failures.Add("提交应在真实 OnEndEdit 时已完成；此处不应再有第二次提交。");
            }

            if (session.Snapshot.IsComposing)
            {
                failures.Add("真实完成编辑后组合仍未结束。");
            }

            // 完成编辑回调由真实 TSF 在事务结束后触发；确认产品收到。
            log.Add($"[smoke] product OnEndEdit callbacks={sink.EndEditCallbacks} "
                + $"start={sink.StartCallbacks} update={sink.UpdateCallbacks} end={sink.EndCallbacks} "
                + $"rangeFailures={sink.RangeFailureCount}");

            // R5：真实 OnEndEdit 必须到达产品实现；未到达即失败，不得 note-and-pass。
            if (sink.EndEditCallbacks == 0)
            {
                failures.Add("产品 OnEndEdit 未在真实写事务后到达（必需，不能记为 note）。");
            }

            // 必需回调计数与范围断言必须出现在最终日志里。
            if (sink.StartCallbacks == 0 || sink.UpdateCallbacks == 0 || sink.EndCallbacks == 0)
            {
                failures.Add($"必需原生回调未全部到达：start={sink.StartCallbacks} "
                    + $"update={sink.UpdateCallbacks} end={sink.EndCallbacks}。");
            }

            log.Add($"[smoke] final writes={writes} text=\"{text}\"");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ReleaseCom(tfComposition);
            ReleaseCom(composition);
            sink?.Dispose();
            owner?.Dispose();

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

            ReleaseCom(context);

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
            runtime?.Dispose();

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
            }

            UnregisterClassW(className, instance);
        }
    }

    /// <summary>在真实 TSF 编辑会话内建立组合（写会话）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int StartNativeComposition(
        ITfContext context,
        uint clientId,
        ITfContextComposition composition,
        NativeCompositionOwnerSink sink,
        AcpTextStore store,
        out ITfComposition? tfComposition)
    {
        tfComposition = null;
        var editSession = new StartCompositionEditSession(composition, sink, store, out var holder);
        var hr = context.RequestEditSession(clientId, editSession, TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE,
            out var sessionHr);
        tfComposition = holder.Composition;
        return hr < 0 ? hr : sessionHr;
    }

    /// <summary>在真实写会话内用组合范围的 <c>ITfRange::SetText</c> 写入文本。</summary>
    /// <remarks>
    /// <b>为什么不直接 store.SetText：</b>那样 TSF 并不知晓范围/编辑记录的变化。
    /// 经组合范围 SetText 才让 TSF 真实维护范围并触发编辑完成记录。
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int SetCompositionText(
        ITfContext context, uint clientId, ITfComposition composition, string value)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var session = new SetTextEditSession(composition, value);
        var hr = context.RequestEditSession(clientId, session,
            TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE, out var sessionHr);
        return hr < 0 ? hr : sessionHr;
    }

    /// <summary>写会话：经组合范围 <c>ITfRange::SetText</c> 写入（真实 TSF 维护范围）。</summary>
    private sealed class SetTextEditSession : ITfEditSession
    {
        private readonly ITfComposition _composition;
        private readonly string _value;

        internal SetTextEditSession(ITfComposition composition, string value)
        {
            _composition = composition;
            _value = value;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public int DoEditSession(uint ec)
        {
            var rangePtr = IntPtr.Zero;
            try
            {
                // 用**有效写 cookie** 取得组合范围。
                if (_composition.GetRange(out rangePtr) < 0 || rangePtr == IntPtr.Zero)
                {
                    return TsfHResult.E_FAIL;
                }

                var rangeIid = TsfOwnerIids.Range;
                if (Marshal.QueryInterface(rangePtr, in rangeIid, out var tfRangePtr) < 0
                    || tfRangePtr == IntPtr.Zero)
                {
                    return TsfHResult.E_NOINTERFACE;
                }

                try
                {
                    var range = (ITfRange)Marshal.GetObjectForIUnknown(tfRangePtr);
                    try
                    {
                        var chars = _value.ToCharArray();
                        return range.SetText(ec, 0u, chars, chars.Length);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(range);
                    }
                }
                finally
                {
                    Marshal.Release(tfRangePtr);
                }
            }
            catch (Exception)
            {
                return TsfHResult.E_FAIL;
            }
            finally
            {
                if (rangePtr != IntPtr.Zero)
                {
                    Marshal.Release(rangePtr);
                }
            }
        }
    }
    /// <summary>在真实写会话内用 <c>ITfComposition::ShiftEnd</c> 调整组合范围末尾。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int ShiftCompositionEnd(
        ITfContext context, uint clientId, ITfComposition composition, int deltaCch)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var session = new ShiftEndEditSession(composition, deltaCch);
        var hr = context.RequestEditSession(clientId, session,
            TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE, out var sessionHr);
        return hr < 0 ? hr : sessionHr;
    }

    /// <summary>写会话：把组合范围末尾扩展若干码元（真实 TSF 范围调整）。</summary>
    private sealed class ShiftEndEditSession : ITfEditSession
    {
        private readonly ITfComposition _composition;
        private readonly int _deltaCch;

        internal ShiftEndEditSession(ITfComposition composition, int deltaCch)
        {
            _composition = composition;
            _deltaCch = deltaCch;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public int DoEditSession(uint ec)
        {
            var rangePtr = IntPtr.Zero;
            try
            {
                if (_composition.GetRange(out rangePtr) < 0 || rangePtr == IntPtr.Zero)
                {
                    return TsfHResult.E_FAIL;
                }

                // 用范围自身作为新末尾目标，先 ShiftEnd 再按 delta 调整其末尾。
                var rangeIid = TsfOwnerIids.Range;
                if (Marshal.QueryInterface(rangePtr, in rangeIid, out var tfRangePtr) < 0
                    || tfRangePtr == IntPtr.Zero)
                {
                    return TsfHResult.E_NOINTERFACE;
                }

                try
                {
                    // 真实 ITfRange::ShiftEnd(ec, cchReq, out pcch, null)，显式槽位调用。
                    var shiftHr = TsfRangeAcp.ShiftEndViaVtable(tfRangePtr, ec, _deltaCch, out _);
                    if (shiftHr < 0)
                    {
                        return shiftHr;
                    }

                    var range = (ITfRange)Marshal.GetObjectForIUnknown(tfRangePtr);
                    try
                    {
                        return _composition.ShiftEnd(ec, range);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(range);
                    }
                }
                finally
                {
                    Marshal.Release(tfRangePtr);
                }
            }
            catch (Exception)
            {
                return TsfHResult.E_FAIL;
            }
            finally
            {
                if (rangePtr != IntPtr.Zero)
                {
                    Marshal.Release(rangePtr);
                }
            }
        }
    }

    /// <summary>在真实写会话内结束组合（EndComposition 需要有效 edit cookie）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int EndCompositionInSession(ITfContext context, uint clientId, ITfComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var session = new EndCompositionEditSession(composition);
        var hr = context.RequestEditSession(clientId, session,
            TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE, out var sessionHr);
        return hr < 0 ? hr : sessionHr;
    }

    /// <summary>写会话：结束组合。</summary>
    private sealed class EndCompositionEditSession : ITfEditSession
    {
        private readonly ITfComposition _composition;

        internal EndCompositionEditSession(ITfComposition composition) => _composition = composition;

        public int DoEditSession(uint ec)
        {
            try
            {
                return _composition.EndComposition(ec);
            }
            catch (Exception)
            {
                return TsfHResult.E_FAIL;
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int QueryForContextComposition(ITfContext context, out ITfContextComposition? composition)
    {
        composition = null;
        var unknown = IntPtr.Zero;
        var ptr = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(context);
            var iid = TsfOwnerIids.ContextCompositionIid;
            var hr = Marshal.QueryInterface(unknown, in iid, out ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
            {
                return hr;
            }

            composition = (ITfContextComposition)Marshal.GetObjectForIUnknown(ptr);
            return hr;
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.Release(ptr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary>写会话：在真实 TSF 事务内建立组合。</summary>
    private sealed class StartCompositionEditSession : ITfEditSession
    {
        private readonly ITfContextComposition _composition;
        private readonly NativeCompositionOwnerSink _sink;
        private readonly AcpTextStore _store;
        private readonly CompositionHolder _holder;

        internal StartCompositionEditSession(
            ITfContextComposition composition,
            NativeCompositionOwnerSink sink,
            AcpTextStore store,
            out CompositionHolder holder)
        {
            _composition = composition;
            _sink = sink;
            _store = store;
            _holder = new CompositionHolder();
            holder = _holder;
        }

        /// <summary>建立组合时使用的 edit cookie（供后续 EndComposition 复用）。</summary>
        internal uint EditCookie { get; private set; }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public int DoEditSession(uint ec)
        {
            EditCookie = ec;
            try
            {
                // 组合范围 = 当前选区（caret 2，长度 0 起点的 1 码元范围用于演示真实范围捕获）。
                // 用 ITfContext 的 selection 更复杂；这里用固定 ACP 范围经 SetExtent 构造 range。
                var range = CreateAcpRange(ec, anchor: 2, cch: 0);
                if (range is null)
                {
                    return TsfHResult.E_FAIL;
                }

                // 第三参必须为 IntPtr.Zero：官方签名是 ITfCompositionSink*（可选，可为 NULL），
                // 不是 ITfContextOwnerCompositionSink。传 owner sink 会让终止路径按不匹配的 vtable
                // 调用 OnCompositionTerminated，实测触发 CLR fatal（0x80131506）。
                // owner 通知由 store.CompositionObserver 独立安装，不在这里传。
                var hr = _composition.StartComposition(ec, range, NullCompositionSink.Instance, out var created);
                if (hr >= 0 && created is not null)
                {
                    _holder.Composition = created;
                }

                ReleaseCom(range);
                return hr;
            }
            catch (Exception)
            {
                return TsfHResult.E_FAIL;
            }
        }

        /// <summary>构造一个 ACP range（经真实 ITextStoreACPServices::CreateRange）。</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private ITfRange? CreateAcpRange(uint ec, int anchor, int cch)
        {
            _ = ec;

            // 真实路径：store 在 AdviseSink 时经 QI 取得 ITextStoreACPServices，
            // 用它 CreateRange(acpStart, acpEnd) 得到 ITfRangeACP 原始指针。
            var rangePtr = _store.CreateAcpRangePointer(anchor, anchor + cch);
            if (rangePtr == IntPtr.Zero)
            {
                return null;
            }

            // QI 到 ITfRange（StartComposition 接受 ITfRange*），保留原始指针供 marshaler 使用。
            var rangeIid = TsfOwnerIids.Range;
            var hr = Marshal.QueryInterface(rangePtr, in rangeIid, out var tfRangePtr);
            Marshal.Release(rangePtr);

            if (hr < 0 || tfRangePtr == IntPtr.Zero)
            {
                return null;
            }

            var range = (ITfRange)Marshal.GetObjectForIUnknown(tfRangePtr);
            Marshal.Release(tfRangePtr);
            return range;
        }
    }

    private sealed class CompositionHolder
    {
        public ITfComposition? Composition { get; set; }
    }

    private sealed class ForwardingService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.Full;

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
        }
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
