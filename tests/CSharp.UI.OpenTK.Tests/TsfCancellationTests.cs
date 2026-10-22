using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a <b>最后取消边界</b>：宿主取消必须经 owner 终止原生组合，而不是直接取消核心客户端。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这一组：</b>既有 cancel-terminal 审计只验证“核心取消之后零写入”，完全没有覆盖原生侧。
/// 直接 <c>client.CancelComposition()</c> 会让原生 TSF 组合继续存活，随后的真实
/// <c>OnEndComposition</c>/完成编辑可能把旧预编辑写回绑定。
/// </para>
/// <para><b>覆盖：</b></para>
/// <list type="number">
/// <item><description>核心先取消、再到 <c>service.RequestCancel()</c>（真实 <see cref="TextField"/> 链路）。</description></item>
/// <item><description>原生真实组合终止：复用 <c>TsfCompositionOwnerSmokeTests</c> 的真实 edit-session 模式；
/// 未能真实建立/终止时<b>失败</b>，绝不声称通过。</description></item>
/// <item><description>迟到的 End/完成编辑不写绑定。</description></item>
/// <item><description>取消后普通 <c>x</c> 与下一段中文组合仍可用（不靠永久 resync 拒绝掩盖）。</description></item>
/// <item><description>busy（宿主工作中）时取消延后且<b>零写入</b>，随后真实安全阶段执行。</description></item>
/// </list>
/// <para>
/// <b>不使用复制逻辑：</b>owner/session/store/桥都是生产实现；客户端是真实 <see cref="TextField"/> 经
/// 真实 <see cref="UiRuntime"/> 建立的 <see cref="ITextInputClient"/>。
/// </para>
/// <para>
/// <b>不做的事：</b>不显示窗口、不注入键盘、不改全局输入法配置、不夺取前台焦点、不做真实微软拼音验收（P4）。
/// </para>
/// </remarks>
public sealed class TsfCancellationTests
{
    private const int WS_OVERLAPPED = 0x00000000;

    /// <summary>窗口类名唯一化序号（同一进程内多个 fixture 各自注册/注销，避免类名污染）。</summary>
    private static int _instanceSeq;

    // ---------------------------------------------------------------- 1) 核心先取消 → service.RequestCancel

    /// <summary>
    /// 在<b>独立 STA 线程</b>上完整创建/使用/销毁真实 fixture（xunit runner 线程是 MTA，不能就地改套间）。
    /// </summary>
    /// <param name="body">在 STA 线程内执行的真实用例体；抛出的异常会回传到调用线程。</param>
    /// <remarks>
    /// <b>超时 45s：</b>类级测试上限，内部 Join 不得超过外层；超时即失败（不静默通过）。
    /// </remarks>
    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "STA 用例超时（45s）。");
        if (failure is not null)
        {
            throw new InvalidOperationException($"STA 用例失败：{failure.Message}", failure);
        }
    }

    /// <summary>
    /// 真实 TextField：核心先本地取消，再到安全阶段 <c>service.RequestCancel()</c>；
    /// 迟到的 End/完成编辑不得写绑定，且基线追上真实回读（不是永久 resync）。
    /// </summary>
    [Fact]
    public void 核心先取消再到service请求取消时迟到End与完成编辑不写绑定()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");
            Assert.Equal("ABCDni", host.Owner.Snapshot.DisplayText);
            Assert.Equal(0, host.SetterCalls);

            // 核心先本地取消（与 Esc/宿主取消一致）：预编辑立即消失、恢复组合前选区，且零写入。
            Assert.True(host.Client.CancelComposition(), "核心本地取消被拒绝。");
            Assert.False(host.Client.IsComposing);
            Assert.Equal(0, host.SetterCalls);
            Assert.Equal("ABCD", host.Store.Text);

            // 核心随后在安全阶段请求平台取消：真实 ITextInputService.RequestCancel 路径。
            // ProcessPendingWork 已经消费了核心排队的“请求平台取消”（这就是真实生产路径），
            // 因此此处只校验它确实到达服务且取消已真实执行一次，不重复断言调用次数。
            host.Runtime.ProcessPendingWork();
            Assert.True(host.Service.RequestCancelCalls >= 1, "核心的取消请求没有到达服务。");
            Assert.True(host.Service.RequestCancel(), "宿主取消没有被真实执行。");

            var owner = host.Owner;

            // 取消是幂等的：核心排队的取消与本次显式请求都可以各执行一次，
            // 关键是**至少执行一次**且队列已清空、没有溢出。
            Assert.True(owner.CancelAppliedCount >= 1, "宿主取消没有被真实执行。");
            Assert.Equal(0, owner.CancelOverflowCount);
            Assert.Equal(0, owner.PendingCancelCount);

            // 迟到 End（原生终止的真实后果）与完成编辑：不得把旧预编辑写回绑定。
            owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", hasRange: true);
            owner.CaptureCompletedEdit(textChanged: true);
            var drain = owner.DrainAtSafePhase();

            Assert.False(drain.AnyCommitted, "取消之后的迟到 End/完成编辑被当成提交。");
            Assert.Equal(0, host.SetterCalls);
            Assert.Equal("ABCD", host.Store.Text);
            Assert.False(host.Owner.Snapshot.IsComposing);

            // 已提交基线必须追上真实回读（不是靠永久 resync 拒绝掩盖未清状态）。
            Assert.False(owner.NeedsResync, "取消把 owner 推进了永久 resync。");
            Assert.Equal("ABCD", owner.Baseline.CommittedText);
            Assert.Equal(host.Version, host.Client.Document.Version);
        });

    // ---------------------------------------------------------------- 1b) 失活栅栏：意图 ticket 分界

    /// <summary>
    /// 窗口失活（<c>WM_ACTIVATE(WA_INACTIVE)</c>）登记的取消意图，必须在同一安全阶段排空前
    /// 使**随后捕获**的 End/完成编辑终结事实不再把预编辑写回绑定（协议层 ticket 分界断言）。
    /// </summary>
    /// <remarks>
    /// 真实顺序（deactivate-trace 证实）：WA_INACTIVE 先于 TIP 的 OnEndComposition/OnEndEdit 捕获。
    /// 意图 ticket 因此低于终结事实；drain 内按 ticket 分界先执行意图（清组合上下文），
    /// 再排空的终结事实没有活动组合上下文可判 Commit。
    /// </remarks>
    [Fact]
    public void 失活栅栏意图先于终结事实时预编辑零写()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");
            Assert.Equal("ABCDni", host.Owner.Snapshot.DisplayText);
            Assert.Equal(0, host.SetterCalls);

            // 等价于 TsfHostSession 失活窗口过程登记的意图：只入队（wnd-proc 内不得终止 COM），
            // ticket 低于随后捕获的失活终结事实。
            _ = host.Owner.RequestCancel(executeNow: false, deactivationFence: true);
            Assert.True(host.Owner.PendingCancelCount >= 1, "失活意图未登记到 owner。");

            // 失活触发的原生终结事实（TIP 自行 EndComposition + OnEndEdit 提交决定）：
            // 捕获时刻物理焦点已离开本 hwnd（真实 minimize 取证：focus=0x0/focusIsHwnd=False）。
            host.SimulatedDeactivated = true;
            host.Owner.Capture(NativeCompositionFactKind.Update, 4, 5, "nihao", hasRange: true);
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 5, "nihao", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);

            // session.DrainSafePhase：前置只执行 ticket<首事实的意图；drain 内分界执行本意图，
            // 其后的同 epoch 事实按“已取消段残留”拒绝（不 Begin、不 Commit）。
            var drain = host.Session.DrainSafePhase();

            Assert.False(drain.AnyCommitted, "失活终结事实被当成提交写回绑定。");
            Assert.Equal(0, host.SetterCalls);
            Assert.False(host.Owner.Snapshot.IsComposing);
            Assert.Equal(0, host.Owner.PendingCancelCount);
        });

    /// <summary>
    /// 失活边界**之前**已捕获但尚未应用的独立结果不受影响：它与栅栏意图、边界后终结事实
    /// 同批排空时仍恰好提交一次；意图只按 ticket 阻断其后的事实，不回滚、不重复、不误拦。
    /// </summary>
    /// <remarks>
    /// 这是“意图整批先跑会误伤 fence 前合法结果”的反向回归：意图必须在 drain 内按
    /// 真实到达 ticket 分界执行，不能在 batch 前无条件执行（plan:118 边界按真实失活时刻计）。
    /// </remarks>
    [Fact]
    public void 失活栅栏前的待决独立结果在同批排空恰好提交一次()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");
            Assert.Equal(0, host.SetterCalls);

            // 边界前独立结果：End + 完成编辑已捕获但**留 pending 不 drain**（fence 前 ticket）。
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);

            // 失活栅栏意图登记（等价于 WM_ACTIVATE 时刻，ticket 介于两批事实之间），
            // 随后失活触发的终结事实 ticket 更高。
            _ = host.Owner.RequestCancel(executeNow: false, deactivationFence: true);
            host.SimulatedDeactivated = true;
            host.Owner.Capture(NativeCompositionFactKind.Start, 6, 3, "hao", hasRange: true);
            host.Owner.Capture(NativeCompositionFactKind.End, 6, 3, "hao", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);

            var drain = host.Session.DrainSafePhase();

            // 边界前已确认的独立结果恰好提交一次；边界后的失活终结事实零写。
            Assert.True(drain.Applied, "安全阶段没有真实应用。");
            Assert.True(drain.AnyCommitted, "边界前已确认的独立结果被误拦。");
            Assert.Equal(1, host.SetterCalls);
            Assert.Equal("ABCDni", host.Store.Text);
        });

    /// <summary>
    /// 失活栅栏真实执行后，同段<b>迟到的 Start 与结果</b>一律零写：
    /// Start 不得借"新组合"复活写权限（失活栅栏=owner 生命周期边界，新组合在新 epoch 下自然无 fence）。
    /// </summary>
    [Fact]
    public void 失活栅栏后同段迟到的Start与结果零写()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");

            // 失活栅栏意图先于失活触发的终结事实登记（等价 WM_ACTIVATE 先到路径）。
            _ = host.Owner.RequestCancel(executeNow: false, deactivationFence: true);

            // 失活段事实捕获时物理焦点已离开（取证判据）。
            host.SimulatedDeactivated = true;
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);
            var drain = host.Session.DrainSafePhase();
            Assert.Equal(0, host.SetterCalls);
            Assert.False(drain.AnyCommitted);

            // 同段迟到的 Start + Result（旧组合残留伪装新段，仍属失活段）：
            // 补真实原生增量使 store 镜像真实，走 CompleteEdit 提交路径，仍须零写。
            host.NativeInsert(4, "hao");
            host.Owner.Capture(NativeCompositionFactKind.Start, 4, 3, "hao", hasRange: true);
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 3, "hao", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);
            var late = host.Session.DrainSafePhase();

            Assert.True(host.SetterCalls == 0, "失活栅栏后同段迟到 Start/结果写入绑定。");
            Assert.False(late.AnyCommitted);
            Assert.Equal("ABCD", host.Store.Text);
        });

    /// <summary>
    /// <b>焦点恢复不解除 owner 栅栏：</b>失活栅栏应用后，物理焦点已回到 hwnd
    /// （<c>SimulatedDeactivated=false</c>）时旧 owner 迟到的 Start+End+EditCompleted
    /// 仍须零写——false 快照只豁免 fence 前已确认的独立结果，不授权 fence 后旧 owner 事实。
    /// </summary>
    [Fact]
    public void 失活栅栏后焦点恢复不解除旧owner写入零()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");

            _ = host.Owner.RequestCancel(executeNow: false, deactivationFence: true);
            host.SimulatedDeactivated = true;
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);
            var drain = host.Session.DrainSafePhase();
            Assert.Equal(0, host.SetterCalls);
            Assert.False(drain.AnyCommitted);

            // 物理焦点已回到 hwnd（deactivated=false），但迟到事实 ticket 仍晚于 fence：
            // 同 epoch 旧 owner 事实一律拒——焦点恢复不构成新写授权。
            host.SimulatedDeactivated = false;
            host.NativeInsert(4, "hao");
            host.Owner.Capture(NativeCompositionFactKind.Start, 4, 3, "hao", hasRange: true);
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 3, "hao", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);
            var late = host.Session.DrainSafePhase();

            Assert.True(host.SetterCalls == 0, "焦点恢复后旧 owner 迟到事实仍写入绑定。");
            Assert.False(late.AnyCommitted);
            Assert.Equal("ABCD", host.Store.Text);
        });

    // ---------------------------------------------------------------- 2) 取消后普通 x + 下一段中文

    /// <summary>
    /// 取消之后：普通字符 <c>x</c> 与下一段中文组合都必须仍然可用（保留新的合法组合能力）。
    /// </summary>
    [Fact]
    public void 取消后普通字符与下一段中文组合仍可用()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");
            Assert.True(host.Client.CancelComposition());
            host.Runtime.ProcessPendingWork();
            Assert.True(host.Service.RequestCancel(), "宿主取消没有被真实执行。");

            // 迟到的原生终结事实（真实终止会同步触发 OnEndComposition）。
            host.Owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", hasRange: true);
            host.Owner.DrainAtSafePhase();
            Assert.Equal(0, host.SetterCalls);

            // ---- 普通字符 x（真实运行时文本输入派发，不经组合）----
            host.Runtime.DispatchTextInput(new TextInputEvent("x"));
            host.Runtime.ProcessPendingWork();

            Assert.Equal("ABCDx", host.Store.Text);
            Assert.Equal(1, host.SetterCalls);
            Assert.False(host.Owner.NeedsResync, "普通输入后 owner 进入 resync。");

            // 普通输入后的基线同步必须成功，否则下一段原生结果会被当外部失效拒绝。
            Assert.True(host.Owner.SynchronizeCommittedBaseline(), "普通输入后基线同步失败。");
            Assert.False(host.Owner.NeedsResync);

            // ---- 下一段中文组合 ----
            host.SelectRange(5, 5);
            host.StartComposition(5, 2, "你好");
            Assert.Equal("ABCDx你好", host.Owner.Snapshot.DisplayText);
            Assert.True(host.Owner.Snapshot.IsComposing, "取消后无法再开始新的组合。");

            // 真实原生结果：预编辑 "你好" 被确认（原生文字不变，End 后由完成编辑按位置判定为确认）。
            host.Owner.Capture(NativeCompositionFactKind.End, 5, 2, "你好", hasRange: true);
            host.Owner.CaptureCompletedEdit(textChanged: true);
            var commit = host.Owner.DrainAtSafePhase();

            Assert.True(commit.AnyCommitted, "取消后的下一段中文组合没有被提交。");
            Assert.Equal("ABCDx你好", host.Store.Text);
            Assert.Equal(2, host.SetterCalls);
            Assert.False(host.Owner.Snapshot.IsComposing);
        });

    // ---------------------------------------------------------------- 3) busy 延期：零写入

    /// <summary>
    /// 宿主工作中（真实 <see cref="TsfInputBridge.HostWorkGuard"/>）时取消只排队、<b>零写入</b>；
    /// 随后真实安全阶段才执行。
    /// </summary>
    [Fact]
    public void 宿主忙碌时取消延后且零写入随后安全阶段执行()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            var bridge = host.Bridge;

            host.SelectRange(4, 4);
            host.StartComposition(4, 2, "ni");
            Assert.True(host.Client.CancelComposition());

            // 不在这里 Flush：核心本地取消已把“请求平台取消”排到安全阶段，
            // 若先 Flush 就会先真实执行一次取消，无法再验证“忙碌时只排队”。
            // 直接进入忙碌状态，让取消请求在忙碌条件下被登记。
            host.SetHostWorking(true);

            Assert.False(host.Service.RequestCancel(), "宿主忙碌时取消被当成已执行。");
            Assert.Equal(0, host.Owner.CancelAppliedCount);

            // 忙碌期间只登记意图（核心排队的取消 + 本次请求，因此 ≥1 且不执行）。
            Assert.True(host.Owner.PendingCancelCount >= 1, "忙碌时取消意图没有被保留。");

            // 零写入：不写绑定、不终止原生、不取消核心。
            Assert.Equal(0, host.SetterCalls);
            Assert.Equal("ABCD", host.Store.Text);
            Assert.Equal(0, host.Owner.CancelTerminateCount);
            Assert.Equal(0, host.Owner.CancelRejectedCount);

            // 宿主回到安全状态：下一个真实安全阶段执行取消（有界，不轮询）。
            host.SetHostWorking(false);
            Assert.True(bridge.RequestCancel(), "排队中的取消没有在安全阶段执行。");
            Assert.True(host.Owner.CancelAppliedCount >= 1, "排队中的取消没有被真实执行。");
            Assert.Equal(0, host.Owner.PendingCancelCount);
            Assert.Equal(0, host.SetterCalls);
            Assert.Equal("ABCD", host.Store.Text);
        });

    // ---------------------------------------------------------------- 1b) 失活/失焦紧邻序列：EndCore 同步投递取消

    /// <summary>
    /// <b>取消→结束紧邻序列（P4 修复的真实集成）</b>：真实隐藏窗口 + 真实 <see cref="TsfInputBridge"/> +
    /// 真实 <c>ITfComposition</c>（<c>StartComposition</c>/<c>SetText</c> 写会话建立，不是事实捕获）。
    /// 失焦同栈 <c>CancelCompositionForRelease → EndInputSession → EndCore</c> 必须在归属切断后把登记的
    /// 取消意图投递给真实桥（plan:116）：<see cref="TsfInputBridge.RequestCancel"/> → owner 在安全阶段真实
    /// <c>TerminateComposition</c>（<c>S_OK</c> + 同步 <c>OnEndComposition</c> 到达），预编辑零写绑定。
    /// 若 <c>EndCore</c> 仍静默丢弃意图，桥在 Dispose 前收不到取消（断言失败，不声称通过）。
    /// </summary>
    /// <remarks>
    /// 生产等价位：窗口失活/失焦的 <c>CancelTextInput → ReleaseTextInputOwnershipFor</c> 同栈
    /// 先本地取消再结束会话；flush 取消在 owner 为 null 的分支不会发出，EndCore 是唯一投递点。
    /// </remarks>
    [Fact]
    public void 失焦结束会话时登记的取消意图真正交给真实桥()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            host.SelectRange(4, 4);

            // 真实 ITfComposition：经产品桥自己的 context/store 写会话建立，不是 owner.Capture 事实。
            Assert.True(QueryForContextComposition(host.Session.Context, out var compositionApi) >= 0
                && compositionApi is not null, "QI(ITfContextComposition) 失败。");
            Assert.True(
                StartNativeComposition(host.Session.Context, host.Session.ClientId, compositionApi,
                    host.Session.Store, out var tfComposition) >= 0 && tfComposition is not null,
                "真实 StartComposition 失败（不能记为受控事实）。");
            Assert.True(SetCompositionText(host.Session.Context, host.Session.ClientId, tfComposition!, "ni") >= 0,
                "组合范围 SetText 失败。");

            host.Owner.DrainAtSafePhase();
            Assert.True(host.Owner.Snapshot.IsComposing, "真实组合未驱动核心预编辑。");
            Assert.Equal(0, host.SetterCalls);

            var endCallbacksBefore = host.Session.Sink.EndCallbacks;
            var cancelCallsBefore = host.Service.RequestCancelCalls;

            // 真实生产路径：失焦 → CancelCompositionForRelease（登记取消意图）→ EndInputSession。
            // EndCore 必须在归属切断后把意图投递给 Service.RequestCancel（真实桥）。
            host.Runtime.SetFocus(null);
            host.Runtime.ProcessPendingWork();

            Assert.True(host.Service.RequestCancelCalls > cancelCallsBefore,
                "结束会话时登记的取消意图没有到达服务（EndCore 静默丢失）。");

            // 桥在所属线程+安全前置满足时必须真实 TerminateComposition：S_OK + 同步 OnEndComposition。
            Assert.True(host.Owner.LastTerminateAttempted,
                "EndCore 投递的取消没有真实调用 TerminateComposition。");
            Assert.True(host.Owner.LastTerminateHResult >= 0,
                $"TerminateComposition 返回失败 {TsfHResult.Describe(host.Owner.LastTerminateHResult)}。");
            Assert.True(host.Session.Sink.EndCallbacks > endCallbacksBefore,
                "真实终止后产品 OnEndComposition 未到达（未证明真实终止）。");

            // 预编辑被丢弃、零写绑定；迟到终结事实（终止同步产生）不得提交。
            Assert.Equal(0, host.SetterCalls);
            Assert.Equal("ABCD", host.Store.Text);
            Assert.False(host.Owner.Snapshot.IsComposing);
        });

    // ---------------------------------------------------------------- 5) 平台几何失效后的消费者级重发


    /// <summary>
    /// <b>消费者级重发回归（P4 anchor-follow 根因）</b>：真实隐藏窗口 + 真实 <see cref="TsfInputBridge"/> +
    /// 真实 <see cref="TextField"/> 客户端——把窗口真实移动到一个新屏幕原点，
    /// <c>NotifyGeometryInvalidated</c> 清缓存后，客户端按<b>逻辑</b>同值去重本会吞掉重发；
    /// 失效接缝复位后，下一安全阶段的重发必须让消费者看到<b>新屏幕原点</b>的
    /// <see cref="HostGeometryCache.ClientScreen"/>（逻辑几何逐位相同）。不断言内部标志或调用计数。
    /// </summary>
    [Fact]
    public void 平台失效后消费者看到新屏幕原点且逻辑几何不变()
        => RunOnSta(() =>
        {
            using var host = new CancelHost("ABCD");

            var bridge = host.Bridge;

            // 宿主构造期的首批发布发生在 PublishTarget 接线之前（客户端已按同值记录发布）；
            // 先经一次真实失效让同值重发走通，取得基准缓存。
            bridge.NotifyGeometryInvalidated();
            host.Runtime.ProcessPendingWork();
            var before = host.Session.GeometryProvider.Current;
            Assert.NotNull(before);

            // 未失效时同值发布确实去重：provider 仍是同一缓存实例。
            host.Runtime.ProcessPendingWork();
            Assert.Same(before, host.Session.GeometryProvider.Current);

            // 真实移动窗口到新屏幕原点（消费者可见的物理变化）。
            const int dx = 173;
            const int dy = 59;
            Assert.True(MoveWindow(host.Hwnd, dx, dy, 240, 120, true), "真实窗口移动失败。");

            // 平台失效：桥清缓存并对客户端复位去重。纯移动不改变逻辑几何；
            // 若不复位，下一安全阶段的同值发布永远到不了桥（P4 anchor-follow 的真实失败）。
            bridge.NotifyGeometryInvalidated();
            Assert.Null(host.Session.GeometryProvider.Current);

            host.Runtime.ProcessPendingWork();

            // 消费者可见断言：逻辑几何逐位相同；ClientScreen 原点是 ClientToScreen 的屏幕物理像素，
            // 随真实窗口位置平移 (dx,dy)（原点本身不经 PixelScale 换算）。
            var republished = host.Session.GeometryProvider.Current;
            Assert.NotNull(republished);
            Assert.Equal(before!.Logical, republished!.Logical);
            Assert.Equal(before.ClientScreen.Left + dx, republished.ClientScreen.Left);
            Assert.Equal(before.ClientScreen.Top + dy, republished.ClientScreen.Top);
        });

    // ---------------------------------------------------------------- 4) 原生真实组合终止

    /// <summary>
    /// 真实隐藏窗口 + 真实 <c>ITfThreadMgr</c>/context/store + 真实 <c>StartComposition</c>：
    /// 宿主取消必须经 <c>ITfContextOwnerCompositionServices::TerminateComposition</c> 终止原生组合、
    /// 同步触发产品 <c>OnEndComposition</c>，零绑定写入，并且之后仍能建立新的合法组合。
    /// </summary>
    [Fact]
    public void 原生真实组合经owner终止并同步触发OnEndComposition且零写入()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：没有可调用的 ole32/msctf，原生终止不适用。
            return;
        }

        var failures = new List<string>();
        var log = new List<string>();
        var thread = new Thread(() => RunNativeTerminationProbe(failures, log));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "原生取消终止探测超时（40s，含在类级 45s 内）。");

        foreach (var line in log)
        {
            Console.WriteLine(line);
        }

        Assert.True(failures.Count == 0, "原生取消终止失败：" + string.Join(" | ", failures));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunNativeTerminationProbe(List<string> failures, List<string> log)
    {
        var className = $"CSharp.UI.P3a.Cancellation.{Environment.ProcessId}.{Interlocked.Increment(ref _instanceSeq)}";
        var instance = GetModuleHandleW(null);

        // 委托必须强根保活到 DestroyWindow/UnregisterClass（见 CancelHost 的同一说明）。
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
        AcpTextStore? store = null;
        NativeCompositionOwner? owner = null;
        NativeCompositionOwnerSink? sink = null;
        ITfThreadMgr? threadMgr = null;
        ITfDocumentMgr? documentMgr = null;
        ITfContext? context = null;
        ITfContextComposition? compositionApi = null;
        ITfComposition? nativeComposition = null;
        ITfComposition? secondComposition = null;
        var text = "ABCD";
        var writes = 0;
        var service = new ForwardingService();

        try
        {
            hwnd = CreateWindowExW(0, className, "CSharp.UI P3a cancellation (hidden)", WS_OVERLAPPED,
                0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                failures.Add($"CreateWindowExW 失败 Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            // 真实 TextField 权威（不是 fake 客户端）。
            runtime = new UiRuntime(
                new TextField(new Binding<string>(() => text, v => { writes++; text = v; })),
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
                failures.Add("真实 TextField 未建立文本输入客户端。");
                return;
            }

            if (!((ITextInputSelectionTarget)client).TrySetCommittedSelection(
                    client.Session, client.Document.Version, new TextSelection(4, 4)))
            {
                failures.Add("设置已提交选区失败。");
                return;
            }

            runtime.ProcessPendingWork();

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

            if (documentMgr.CreateContext(clientId, 0, store, out context, out _) < 0 || context is null)
            {
                failures.Add("CreateContext 失败（msctf 未接受 store 作为 ITextStoreACP）。");
                return;
            }

            if (documentMgr.Push(context) < 0)
            {
                failures.Add("Push 失败。");
                return;
            }

            var focusHr = threadMgr.SetFocus(documentMgr);
            var assocHr = threadMgr.AssociateFocus(hwnd, documentMgr, out var prevDm);
            if (prevDm is not null)
            {
                ReleaseCom(prevDm);
            }

            log.Add($"[cancel] focus hr=0x{focusHr:X8} assoc hr=0x{assocHr:X8}");

            if (sink.AdviseEditSink(context) < 0 || !sink.EditSinkAdvised)
            {
                failures.Add("ITfTextEditSink.AdviseSink 失败（必需）。");
                return;
            }

            if (QueryForContextComposition(context, out compositionApi) < 0 || compositionApi is null)
            {
                failures.Add("QI(ITfContextComposition) 失败（必需）。");
                return;
            }

            // owner 侧终止必须使用本会话真实 context（借用）。
            owner.BindNativeContext(context);

            var startHr = StartNativeComposition(
                context, clientId, compositionApi, store, out nativeComposition, log);
            if (startHr < 0 || nativeComposition is null)
            {
                failures.Add($"真实 StartComposition 失败 hr=0x{startHr:X8}（不能记为 note）。");
                return;
            }

            log.Add($"[cancel] composition started start={sink.StartCallbacks} range={sink.LastRange}");

            // 预编辑进入核心（零写入）。
            owner.DrainAtSafePhase();
            if (!session.Snapshot.IsComposing)
            {
                failures.Add("真实 StartComposition 未驱动核心组合（没有活动组合可取消）。");
                return;
            }

            if (writes != 0)
            {
                failures.Add($"预编辑阶段写入了 Binding（writes={writes}）。");
                return;
            }

            // 真实原生写入：经组合范围 SetText（TSF 自己维护范围与编辑记录）。
            var insertHr = SetCompositionText(context, clientId, nativeComposition, "nihao");
            if (insertHr < 0)
            {
                failures.Add($"组合范围 SetText 失败 hr=0x{insertHr:X8}。");
                return;
            }

            owner.DrainAtSafePhase();
            log.Add($"[cancel] preedit display=\"{session.Snapshot.DisplayText}\" writes={writes}");

            if (writes != 0)
            {
                failures.Add($"真实预编辑更新写入了 Binding（writes={writes}）。");
                return;
            }

            var endCallbacksBefore = sink.EndCallbacks;

            // ---- 宿主取消：必须经 owner 终止原生组合 ----
            var cancelApplied = owner.RequestCancel();

            log.Add($"[cancel] RequestCancel applied={cancelApplied} "
                + $"terminateCalls={owner.CancelTerminateCount} "
                + $"attempted={owner.LastTerminateAttempted} "
                + $"lastHr=0x{owner.LastTerminateHResult:X8}"
                + $"({TsfHResult.Describe(owner.LastTerminateHResult)}) "
                + $"endCallbacks={sink.EndCallbacks}(before={endCallbacksBefore}) "
                + $"terminalObserved={owner.LastTerminateTerminalObserved} "
                + $"unavailable={owner.CancelTerminateUnavailableCount} "
                + $"failures={owner.CancelTerminateFailureCount}");

            if (!owner.LastTerminateAttempted || !owner.HasTerminateHResult)
            {
                failures.Add("没有真实调用 TerminateComposition（不能声称原生终止）："
                    + $"unavailable={owner.CancelTerminateUnavailableCount}。");
                return;
            }

            if (owner.LastTerminateHResult < 0)
            {
                failures.Add($"TerminateComposition 返回失败 "
                    + $"{TsfHResult.Describe(owner.LastTerminateHResult)}。");
                return;
            }

            // 官方：TerminateComposition 会同步触发 OnEndComposition。必须真实到达。
            if (sink.EndCallbacks <= endCallbacksBefore)
            {
                failures.Add("原生终止之后产品 OnEndComposition 未到达（未证明真实终止）。");
                return;
            }

            if (!owner.LastTerminateTerminalObserved)
            {
                failures.Add("owner 未观察到终结回调（LastTerminateTerminalObserved=false）。");
                return;
            }

            // 核心已回到已提交文本，且零写入。
            if (session.Snapshot.IsComposing)
            {
                failures.Add("取消之后核心仍处于组合状态。");
            }

            if (writes != 0)
            {
                failures.Add($"取消写入了 Binding（writes={writes}）。");
            }

            if (!string.Equals(text, "ABCD", StringComparison.Ordinal))
            {
                failures.Add($"取消后已提交文本被改变：\"{text}\"。");
            }

            // 迟到的终结事实（终止同步产生的 End/完成编辑）在安全阶段不得提交。
            var afterCancel = owner.DrainAtSafePhase();
            log.Add($"[cancel] after-cancel drain applied={afterCancel.Applied} "
                + $"committed={afterCancel.AnyCommitted} writes={writes} text=\"{text}\" "
                + $"pending={owner.PendingCount} resync={owner.NeedsResync}");

            if (afterCancel.AnyCommitted)
            {
                failures.Add("取消之后的迟到终结事实被当成提交。");
            }

            if (writes != 0)
            {
                failures.Add($"迟到终结事实写入了 Binding（writes={writes}）。");
            }

            if (owner.NeedsResync)
            {
                failures.Add("取消把 owner 推进了永久 resync（未清状态被掩盖）。");
            }

            var restoredNative = store.ReadAuthoritativeDocument().Text;
            if (restoredNative != "ABCD")
            {
                failures.Add($"取消后原生store未恢复权威文本：\"{restoredNative}\"。");
            }

            // ---- 取消之后下一段真实原生组合仍可建立（保留新的合法组合能力）----
            // 组合范围锚定在当前已提交末尾（caret 4），不替换任何已提交文本。
            var secondStart = StartNativeComposition(
                context, clientId, compositionApi, store, out secondComposition);
            if (secondStart < 0 || secondComposition is null)
            {
                failures.Add($"取消之后无法再建立真实组合 hr=0x{secondStart:X8}。");
                return;
            }

            owner.DrainAtSafePhase();
            if (!session.Snapshot.IsComposing)
            {
                failures.Add("取消之后的第二段组合没有被核心接受。");
                return;
            }

            var secondInsert = SetCompositionText(context, clientId, secondComposition, "好");
            if (secondInsert < 0)
            {
                failures.Add($"第二段组合 SetText 失败 hr=0x{secondInsert:X8}。");
                return;
            }

            owner.DrainAtSafePhase();

            // 真实结束第二段组合（EndComposition 需要有效 edit cookie）。
            var endSecond = EndCompositionInSession(context, clientId, secondComposition);
            log.Add($"[cancel] second composition end hr=0x{endSecond:X8}");
            owner.DrainAtSafePhase();

            log.Add($"[cancel] final writes={writes} text=\"{text}\" "
                + $"terminate={owner.CancelTerminateCount} failures={owner.CancelTerminateFailureCount} "
                + $"unavailable={owner.CancelTerminateUnavailableCount} resync={owner.NeedsResync}");

            if (endSecond < 0 || writes != 1 || text != "ABCD好" || session.Snapshot.IsComposing)
            {
                failures.Add($"取消后下一段结果错误：end=0x{endSecond:X8} writes={writes} text=\"{text}\" composing={session.Snapshot.IsComposing}。");
            }

            // 走到这里说明原生终止已真实发起并成功（上面的 attempted/HRESULT 断言已通过）。
            if (owner.CancelTerminateFailureCount != 0)
            {
                failures.Add($"原生终止失败 {owner.CancelTerminateFailureCount} 次。");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ReleaseCom(secondComposition);
            ReleaseCom(nativeComposition);
            ReleaseCom(compositionApi);
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

            // 窗口与类都已注销之后才允许回收委托根。
            GC.KeepAlive(proc);
        }
    }

    // ---------------------------------------------------------------- 托管取消宿主（真实 TextField）

    /// <summary>
    /// 真实 <see cref="TextField"/> 取消宿主：owner/session/store/桥都是生产实现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 桥是真实 <see cref="TsfInputBridge"/>（<b>它自己</b>的 <see cref="TsfHostSession"/> + 真实隐藏 HWND），
    /// 因此 <see cref="TsfInputBridge.RequestCancel"/> 走的就是生产取消路径；
    /// store 安全阶段与 <see cref="TsfInputBridge.HostWorkGuard"/> 都是真实判定。
    /// </para>
    /// <para>
    /// <b>只有一个会话：</b>同一 HWND 只允许一个写入来源，因此不再额外建立第二个
    /// <see cref="TsfHostSession"/>（那会被 <see cref="HostWriterRegistry"/> 正确拒绝）。
    /// </para>
    /// </remarks>
    private sealed class CancelHost : IDisposable
    {
        public CancelHost(string initial)
        {
            Store = new ScriptedBinding(initial);
            Service = new ForwardingService();

            Runtime = new UiRuntime(
                new TextField(Store.Binding),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
                    TextureService = new RecordingTextureService(),
                    TextInputService = Service,
                });

            Runtime.SetViewport(new Size(400, 120), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.SetFocus(Runtime.Root);
            Runtime.BuildFrame();

            Client = Service.ActiveClient ?? throw new InvalidOperationException("会话未建立。");

            // 窗口类名唯一（同一进程内多个用例各自注册/注销，避免类名污染）。
            _className = $"CSharp.UI.P3a.CancelHost.{Environment.ProcessId}.{Interlocked.Increment(ref _instanceSeq)}";
            _instance = GetModuleHandleW(null);

            // 委托必须由实例字段强根保活到 DestroyWindow/UnregisterClass：
            // 只把函数指针交给系统、GC 回收委托后窗口过程会调用到已释放的存根（真实崩溃）。
            _proc = (hWnd, msg, wParam, lParam) => DefWindowProcW(hWnd, msg, wParam, lParam);
            _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);

            var wc = new WndClassW
            {
                WndProc = _procPtr,
                Instance = _instance,
                ClassName = _className,
            };

            if (RegisterClassW(ref wc) == 0)
            {
                Runtime.Dispose();
                throw new InvalidOperationException($"RegisterClassW 失败 Win32={Marshal.GetLastWin32Error()}");
            }

            _classRegistered = true;

            Hwnd = CreateWindowExW(0, _className, "CSharp.UI P3a cancel host (hidden)", WS_OVERLAPPED,
                0, 0, 240, 120, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);

            if (Hwnd == IntPtr.Zero)
            {
                // 构造失败必须释放已建 HWND/窗口类，不能把注册过的类留在进程里污染后续用例。
                Dispose();
                throw new InvalidOperationException($"CreateWindowExW 失败 Win32={Marshal.GetLastWin32Error()}");
            }

            var created = TsfInputBridge.TryCreate(
                Hwnd,
                Client,
                c => ReferenceEquals(c, Service.ActiveClient),
                Store.Text,
                new CandidateOverlay(),
                () => { },
                out var bridge,
                out var reason);

            if (!created || bridge is null)
            {
                Dispose();
                throw new InvalidOperationException($"真实 TSF 桥建立失败：{reason}");
            }

            _bridge = bridge;
            bridge.HostWorkGuard = () => HostWorking;
            bridge.AttachRuntime(Runtime);

            // 生产里核心调用的 Service 就是桥：把真实取消/几何发布路径接上，避免测试绕过桥。
            Service.CancelTarget = () => bridge.RequestCancel();
            Service.PublishTarget = g => bridge.PublishGeometry(g);

            // 隐藏测试 hwnd 从不持有焦点——生产注入的 GetFocus 探针会把它误判为"永久失活"。
            // 协议测试注入确定性"焦点仍在"语义；模拟失活段时翻转为 true（真实 capture 时刻冻结）。
            Owner.CaptureDeactivationProbe = () => SimulatedDeactivated;
        }

        private readonly string _className;
        private readonly IntPtr _instance;
        private readonly WndProcDelegate _proc;
        private readonly IntPtr _procPtr;
        private readonly TsfInputBridge? _bridge;
        private bool _classRegistered;

        public ScriptedBinding Store { get; }

        public ForwardingService Service { get; }

        public UiRuntime Runtime { get; }

        public ITextInputClient Client { get; }

        public IntPtr Hwnd { get; private set; }

        public TsfInputBridge Bridge
            => _bridge ?? throw new InvalidOperationException("桥未建立。");

        /// <summary>生产桥自己的 TSF 会话（取消与 store 安全阶段都在它上面判定）。</summary>
        public TsfHostSession Session => Bridge.Session;

        /// <summary>生产桥自己的原生组合 owner（取消意图与原生终止在这里执行）。</summary>
        public NativeCompositionOwner Owner => Bridge.Session.Owner;

        public AcpTextStore Acp => Bridge.Session.Store;

        /// <summary>注入到 owner 取证探针的"当前焦点状态"（测试可切换模拟失活段）。</summary>
        public bool SimulatedDeactivated { get; set; }

        public bool HostWorking { get; private set; }

        public int SetterCalls => Store.SetterCalls;

        public int Version => Client.Document.Version;

        public void SetHostWorking(bool working) => HostWorking = working;

        public void SelectRange(int start, int end)
        {
            var target = (ITextInputSelectionTarget)Client;
            Assert.True(target.TrySetCommittedSelection(Client.Session, Version, new TextSelection(start, end)));
            Runtime.ProcessPendingWork();
        }

        /// <summary>真实原生文档插入（TSF 写会话效果；不写 Binding）。</summary>
        public void NativeInsert(int acpStart, string value)
            => Acp.Document.TryReplace(acpStart, acpStart, value, AcpEditOrigin.Tsf, out _);

        /// <summary>真实原生组合开始（走产品 <see cref="NativeCompositionOwner.Capture"/>）。</summary>
        public void StartComposition(int anchor, int length, string preedit)
        {
            NativeInsert(anchor, preedit);
            Owner.Capture(NativeCompositionFactKind.Start, anchor, length, preedit, hasRange: true);
            Owner.DrainAtSafePhase();
        }

        /// <summary>幂等释放：桥 → 窗口 → 窗口类 → 运行时（顺序固定，构造失败时也可安全调用）。</summary>
        public void Dispose()
        {
            _bridge?.Dispose();

            if (Hwnd != IntPtr.Zero)
            {
                DestroyWindow(Hwnd);
                Hwnd = IntPtr.Zero;
            }

            if (_classRegistered)
            {
                UnregisterClassW(_className, _instance);
                _classRegistered = false;
            }

            Runtime.Dispose();

            // 委托根保持到窗口与类都注销之后（字段仍在实例上，实例随 using 释放）。
            GC.KeepAlive(_proc);
        }
    }

    private sealed class ScriptedBinding
    {
        public ScriptedBinding(string initial)
        {
            Text = initial;
            Binding = CSharp.UI.Binding<string>.From(() => Text, Write);
        }

        public CSharp.UI.Binding<string> Binding { get; }

        public string Text { get; set; }

        public List<string> SetterValues { get; } = [];

        public int SetterCalls => SetterValues.Count;

        private void Write(string value)
        {
            SetterValues.Add(value);
            Text = value;
        }
    }

    private sealed class ForwardingService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; private set; }

        public int RequestCancelCalls { get; private set; }

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

        /// <summary>
        /// 真实服务入口：生产里 <c>Service</c> 就是桥本身，取消必须经 <see cref="TsfInputBridge.RequestCancel"/>
        /// 走 owner（原生终止 + 核心取消），<b>不</b>直接 <c>CancelComposition</c>。
        /// </summary>
        /// <remarks>桥由测试在建立后注入；未注入时如实返回 false（不伪造成功）。</remarks>
        public bool RequestCancel()
        {
            RequestCancelCalls++;
            return CancelTarget?.Invoke() ?? false;
        }

        /// <summary>真实取消承接者（生产为 TSF 桥；测试在桥建立后注入）。</summary>
        public Func<bool>? CancelTarget { get; set; }

        /// <summary>真实发布承接者（生产为 TSF 桥；测试在桥建立后注入）。</summary>
        public Func<TextInputGeometry, bool>? PublishTarget { get; set; }

        public bool PublishGeometry(TextInputGeometry geometry)
            => PublishTarget?.Invoke(geometry) ?? false;
    }

    // ---------------------------------------------------------------- 原生 edit-session 辅助

    /// <summary>在真实写会话内建立组合（范围锚定在当前已提交末尾，不替换已提交文本）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int StartNativeComposition(
        ITfContext context,
        uint clientId,
        ITfContextComposition composition,
        AcpTextStore store,
        out ITfComposition? tfComposition,
        List<string>? log = null)
    {
        tfComposition = null;
        var editSession = new StartCompositionEditSession(composition, store, out var holder);
        var hr = context.RequestEditSession(
            clientId, editSession, TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE, out var sessionHr);
        tfComposition = holder.Composition;
        log?.Add($"[cancel] start-diag request=0x{hr:X8} session=0x{sessionHr:X8} {editSession.Diagnostic}");
        return hr < 0 ? hr : sessionHr;
    }

    /// <summary>在真实写会话内用组合范围 <c>ITfRange::SetText</c> 写入文本。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int SetCompositionText(
        ITfContext context, uint clientId, ITfComposition composition, string value)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var session = new SetTextEditSession(composition, value);
        var hr = context.RequestEditSession(
            clientId, session, TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE, out var sessionHr);
        return hr < 0 ? hr : sessionHr;
    }

    /// <summary>在真实写会话内结束组合。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int EndCompositionInSession(
        ITfContext context, uint clientId, ITfComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var session = new EndCompositionEditSession(composition);
        var hr = context.RequestEditSession(
            clientId, session, TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE, out var sessionHr);
        return hr < 0 ? hr : sessionHr;
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
    /// <remarks>
    /// <b>第三参必须传 null（官方）：</b><c>ITfContextComposition::StartComposition</c> 的第三参是
    /// <c>ITfCompositionSink*</c>（可选，可为 NULL），<b>不是</b> <c>ITfContextOwnerCompositionSink</c>。
    /// 传 owner sink 会让 TSF 在组合终止时按 <c>ITfCompositionSink::OnCompositionTerminated</c> 的 vtable 调用
    /// 一个布局不匹配的对象，实测在 <c>TerminateComposition</c> 路径上触发 CLR fatal（0x80131506）。
    /// owner 侧通知仍由 <see cref="AcpTextStore.CompositionObserver"/> 独立安装，无需在这里重复传入。
    /// </remarks>
    private sealed class StartCompositionEditSession : ITfEditSession
    {
        private readonly ITfContextComposition _composition;
        private readonly AcpTextStore _store;
        private readonly CompositionHolder _holder;

        internal StartCompositionEditSession(
            ITfContextComposition composition,
            AcpTextStore store,
            out CompositionHolder holder)
        {
            _composition = composition;
            _store = store;
            _holder = new CompositionHolder();
            holder = _holder;
        }

        /// <summary>最近一次建立组合的诊断（脱敏；无用户文本）。</summary>
        internal string Diagnostic { get; private set; } = string.Empty;

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public int DoEditSession(uint ec)
        {
            try
            {
                // 与真实客户端 caret 4 对齐；范围错误会把正常文档尾部误分为结果。
                var rangePtrProbe = _store.CreateAcpRangePointer(4, 4);
                Diagnostic = $"CreateAcpRangePointer(4,4)=0x{rangePtrProbe.ToInt64():X} storeLen={_store.Document.Length}";
                if (rangePtrProbe != IntPtr.Zero)
                {
                    Marshal.Release(rangePtrProbe);
                }

                var range = CreateAcpRange(ec, anchor: 4, cch: 0);
                if (range is null)
                {
                    Diagnostic += " range=null";
                    return TsfHResult.E_FAIL;
                }

                // 类型正确的 TIP sink；owner通知仍由store独立安装的observer接收。
                var hr = _composition.StartComposition(ec, range, NullCompositionSink.Instance, out var created);
                Diagnostic += $" StartComposition hr=0x{hr:X8} created={(created is null ? "null" : "ok")}";
                if (hr >= 0 && created is not null)
                {
                    _holder.Composition = created;
                }

                ReleaseCom(range);
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostic += $" {ex.GetType().Name}";
                return TsfHResult.E_FAIL;
            }
        }

        /// <summary>构造 ACP range（经真实 <c>ITextStoreACPServices::CreateRange</c>）。</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private ITfRange? CreateAcpRange(uint ec, int anchor, int cch)
        {
            _ = ec;

            var rangePtr = _store.CreateAcpRangePointer(anchor, anchor + cch);
            if (rangePtr == IntPtr.Zero)
            {
                return null;
            }

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

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
