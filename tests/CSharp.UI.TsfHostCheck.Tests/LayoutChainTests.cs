using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// `--trace-layout-chain` 的纯逻辑测试：默认关闭不产生字段/不执行原生查询、通知 enter/exit 配对 ID、
/// 嵌套异常后作用域恢复、应用探针归因恢复、不同 store 之间标签隔离、
/// 成功/失败几何事件都带当前通知 ID。全部不初始化 COM、不创建窗口、不发键。
/// <para>
/// 这些测试只证明诊断自身的配对与归因正确性；**不**证明任何 TIP 身份或候选行为。
/// </para>
/// </summary>
public sealed class LayoutChainTests
{
    private sealed record Entry(string Stage, string Kind, Dictionary<string, object?> Fields);

    private static (AcpTextStore Store, RecordingTextStoreSink Sink, List<Entry> Entries) NewStore(
        bool traceEnabled, TsfTrace? trace = null)
    {
        var entries = new List<Entry>();
        void Recorder(string stage, string kind, IDictionary<string, object?>? fields)
        {
            entries.Add(new Entry(stage, kind,
                fields is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(fields)));
            trace?.Invoke(stage, kind, fields);
        }

        var store = new AcpTextStore(Recorder, new LayoutChainTrace(traceEnabled));
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK,
            store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        return (store, sink, entries);
    }

    [Fact]
    public void DisabledChainAddsNoDiagnosticFieldsAndKeepsOldFields()
    {
        var (store, _, entries) = NewStore(traceEnabled: false);

        Assert.False(store.Chain.Enabled);
        store.GetTextExt(1, 0, 0, out _, out _);

        var rejected = Assert.Single(entries, e => e.Stage == "geometry" && e.Kind == "rejected");
        // 旧字段保持原样。
        Assert.Equal("GetTextExt", rejected.Fields["method"]);
        Assert.Equal("TS_E_NOLOCK", rejected.Fields["hresult"]);
        // 关闭时不得出现任何诊断字段（否则会改变既有日志形状）。
        Assert.False(rejected.Fields.ContainsKey("diag_scope"));
        Assert.False(rejected.Fields.ContainsKey("diag_notification_id"));
        Assert.False(rejected.Fields.ContainsKey("diag_lock_request_id"));
    }

    [Fact]
    public void DisabledChainStillReturnsUnknownScopeWithoutSideEffects()
    {
        var disabled = LayoutChainTrace.Disabled;

        Assert.False(disabled.Enabled);
        Assert.Equal(LayoutChainScope.Unknown, disabled.DescribeScope());
        Assert.Equal(LayoutChainScope.Unknown, disabled.CurrentScope);
        Assert.Equal(0, disabled.CurrentNotificationId);

        // 禁用时 PushScope 是空租约，不改变任何状态。
        using (disabled.PushScope(LayoutChainScope.ViewQuery))
        {
            Assert.Equal(LayoutChainScope.Unknown, disabled.DescribeScope());
        }

        Assert.Equal(LayoutChainScope.Unknown, disabled.DescribeScope());
    }

    [Fact]
    public void DisabledChainDoesNotQueryNativeFocus()
    {
        // 关闭时 Capture 必须立即返回：不产生 diag/layout-chain 记录，也不调用任何 Win32/TSF 查询。
        var entries = new List<Entry>();
        var chain = new LayoutChainTrace(false);

        FocusSnapshot.Capture(chain, (stage, kind, fields) => entries.Add(new Entry(stage, kind,
            fields is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(fields))),
            "test", IntPtr.Zero, null, null, null);

        Assert.Empty(entries);
    }

    [Fact]
    public void LayoutNotificationEnterAndExitShareOnePairedId()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);
        var grant = TsfFlags.TS_LF_READ;
        sink.LockGranted = _ => TsfHResult.S_OK;

        store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        var enter = Assert.Single(entries, e => e.Kind == "layout-enter");
        var exit = Assert.Single(entries, e => e.Kind == "layout-exit");
        var id = enter.Fields["diag_notification_id"];
        Assert.NotNull(id);
        Assert.Equal(id, exit.Fields["diag_notification_id"]);
        Assert.Equal(TsfFlags.TS_LC_CHANGE, (int)enter.Fields["layout_code"]!);
        // exit 在 finally：深度已回到 0（enter 阶段深度为 1）。
        Assert.Equal(0, (int)exit.Fields["layout_notify_depth"]!);
        _ = grant;
    }

    [Fact]
    public void NestedNotificationIdsStayPairedAndUnique()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);

        // 在 OnLayoutChange 内部再触发一次通知：模拟 TSF 回调内重入。
        sink.LockGranted = _ => TsfHResult.S_OK;
        var nested = false;
        sink.LayoutChangeHook = (_, _) =>
        {
            if (nested)
            {
                return;
            }

            nested = true;
            store.NotifyLayoutChange(TsfFlags.TS_LC_CREATE);
        };

        store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        var enters = entries.Where(e => e.Kind == "layout-enter").ToList();
        var exits = entries.Where(e => e.Kind == "layout-exit").ToList();
        Assert.Equal(2, enters.Count);
        Assert.Equal(2, exits.Count);
        // 两个通知 ID 不同，且各自成对（exit 顺序为内层先退出）。
        Assert.NotEqual(enters[0].Fields["diag_notification_id"], enters[1].Fields["diag_notification_id"]);
        var enterIds = enters.Select(e => e.Fields["diag_notification_id"]).ToList();
        var exitIds = exits.Select(e => e.Fields["diag_notification_id"]).ToList();
        Assert.Equal(enterIds.OrderBy(x => x).ToList(), exitIds.OrderBy(x => x).ToList());
        // 最外层 exit 深度回到 0，说明嵌套退出后正确恢复。
        Assert.Equal(0, exits.Min(e => (int)e.Fields["layout_notify_depth"]!));
    }

    [Fact]
    public void NotificationExceptionStillWritesPairedExitAndRestoresState()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);

        sink.LayoutChangeHook = (_, _) => throw new InvalidOperationException("boom");

        var ok = store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        // 异常不穿过：既有语义是返回 false。
        Assert.False(ok);
        Assert.Single(entries, e => e.Kind == "layout-enter");
        // exit 必须在 finally 写出，且通知状态已恢复。
        var exit = Assert.Single(entries, e => e.Kind == "layout-exit");
        Assert.Equal(0, (int)exit.Fields["layout_notify_depth"]!);
        Assert.Equal(0, store.Chain.CurrentNotificationId);

        // 之后的事件不再携带旧通知 ID。
        entries.Clear();
        store.GetTextExt(1, 0, 0, out _, out _);
        var after = Assert.Single(entries, e => e.Kind == "rejected");
        Assert.Null(after.Fields["diag_notification_id"]);
        Assert.Equal(LayoutChainScope.Unknown, after.Fields["diag_scope"]);
    }

    [Fact]
    public void GeometryInsideNotificationIsAttributedAsTsfCallbackWithNotificationId()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);

        // 在 OnLayoutChange 回调内发起几何查询：TSF 的典型同步重入路径。
        sink.LayoutChangeHook = (_, _) =>
        {
            store.GetTextExt(1, 0, 0, out _, out _);
        };

        store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        var enter = Assert.Single(entries, e => e.Kind == "layout-enter");
        var id = enter.Fields["diag_notification_id"];
        var rejected = Assert.Single(entries, e => e.Stage == "geometry" && e.Kind == "rejected");
        // 通知内、非应用探针 → tsf-callback，并带上该通知的 ID。
        Assert.Equal(LayoutChainScope.TsfCallback, rejected.Fields["diag_scope"]);
        Assert.Equal(id, rejected.Fields["diag_notification_id"]);
    }

    [Fact]
    public void SuccessfulAndFailedGeometryBothCarryScopeAndNotificationId()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 }, rowHeight: 20,
            client: TsfRect.FromLtrb(0, 0, 400, 100), originX: 0, originY: 0, notify: false);

        sink.LayoutChangeHook = (_, _) =>
        {
            // 成功路径：读锁已授予（模拟 TSF 在通知内重新取锁）。
            StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ,
                () => store.GetTextExt(1, 0, 1, out _, out _), out _, out _);
            // 失败路径：读锁已释放。
            store.GetTextExt(1, 0, 1, out _, out _);
        };

        store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        var id = entries.Single(e => e.Kind == "layout-enter").Fields["diag_notification_id"];
        // 成功与拒绝都带同一通知 ID 与 tsf-callback 归因。
        var success = entries.Where(e => e.Stage == "geometry" && e.Kind == "text-ext").ToList();
        var failure = entries.Where(e => e.Stage == "geometry" && e.Kind == "rejected").ToList();
        Assert.NotEmpty(success);
        Assert.NotEmpty(failure);        Assert.All(success, e => Assert.Equal(id, e.Fields["diag_notification_id"]));
        Assert.All(success, e => Assert.Equal(LayoutChainScope.TsfCallback, e.Fields["diag_scope"]));
        Assert.All(failure, e => Assert.Equal(id, e.Fields["diag_notification_id"]));
        Assert.All(failure, e => Assert.Equal(LayoutChainScope.TsfCallback, e.Fields["diag_scope"]));
        // 旧字段仍在。
        Assert.Contains(failure, e => Equals(e.Fields["hresult"], "TS_E_NOLOCK"));
    }

    [Fact]
    public void FullyInvisibleZeroRectAndGetWndCarryAttribution()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        // 文本行完全在 client 之外（originY=200 远低于 client 底边 20）：GetTextExt 必须 S_OK + 零矩形。
        StoreTestHelpers.Publish(store, new[] { 10, 10 }, rowHeight: 20,
            client: TsfRect.FromLtrb(0, 0, 400, 20), originX: 0, originY: 200, notify: false);

        sink.LayoutChangeHook = (_, _) =>
        {
            StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
            {
                store.GetTextExt(1, 0, 2, out _, out _);
                store.GetWnd(1, out _);
                return TsfHResult.S_OK;
            }, out _, out _);
        };

        store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        var id = entries.Single(e => e.Kind == "layout-enter").Fields["diag_notification_id"];
        // 中央 Trace 增强确保未逐处改动的路径（fully-invisible-zero-rect）也带归因。
        var invisible = Assert.Single(entries, e => e.Kind == "text-ext-fully-invisible-zero-rect");
        Assert.Equal(id, invisible.Fields["diag_notification_id"]);
        Assert.Equal(LayoutChainScope.TsfCallback, invisible.Fields["diag_scope"]);
    }

    [Fact]
    public void ApplicationScopeRestoresAfterNestedPushAndException()
    {
        var chain = new LayoutChainTrace(true);

        using (chain.PushScope(LayoutChainScope.RequestSession))
        {
            Assert.Equal(LayoutChainScope.RequestSession, chain.DescribeScope());

            try
            {
                using (chain.PushScope(LayoutChainScope.ViewQuery))
                {
                    Assert.Equal(LayoutChainScope.ViewQuery, chain.DescribeScope());
                    throw new InvalidOperationException("boom");
                }
            }
            catch (InvalidOperationException)
            {
                // 内层租约已 Dispose：必须回到 request-session，而不是遗留在 view-query。
                Assert.Equal(LayoutChainScope.RequestSession, chain.DescribeScope());
            }
        }

        // 全部退出后回到 unknown。
        Assert.Equal(LayoutChainScope.Unknown, chain.DescribeScope());
    }

    [Fact]
    public void ApplicationScopeOverridesCallbackAttribution()
    {
        var chain = new LayoutChainTrace(true);
        var id = chain.BeginNotification();
        chain.EnterNotification(id);

        // 即使处于通知内，显式应用探针阶段也必须优先，不能被误标成 tsf-callback。
        Assert.Equal(LayoutChainScope.TsfCallback, chain.DescribeScope());
        using (chain.PushScope(LayoutChainScope.StoreDirectQuery))
        {
            Assert.Equal(LayoutChainScope.StoreDirectQuery, chain.DescribeScope());
        }

        chain.ExitNotification();
        Assert.Equal(LayoutChainScope.Unknown, chain.DescribeScope());
    }

    [Fact]
    public void DifferentStoresKeepIndependentAttribution()
    {
        var first = new LayoutChainTrace(true);
        var second = new LayoutChainTrace(true);

        var firstId = first.BeginNotification();
        first.EnterNotification(firstId);
        using var scope = second.PushScope(LayoutChainScope.ViewQuery);

        // 第二个 store 不受第一个的通知/阶段影响（标签不跨 store 串用）。
        Assert.Equal(firstId, first.CurrentNotificationId);
        Assert.Equal(LayoutChainScope.TsfCallback, first.DescribeScope());
        Assert.Equal(0, second.CurrentNotificationId);
        Assert.Equal(LayoutChainScope.ViewQuery, second.DescribeScope());
    }

    [Fact]
    public void StoreScopedChainDoesNotLeakIntoOtherStoreLogs()
    {
        var (traced, tracedSink, tracedEntries) = NewStore(traceEnabled: true);
        var (plain, _, plainEntries) = NewStore(traceEnabled: false);

        tracedSink.LayoutChangeHook = (_, _) => { };
        traced.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);
        plain.GetTextExt(1, 0, 0, out _, out _);

        Assert.Contains(tracedEntries, e => e.Kind == "layout-enter");
        var plainRejected = Assert.Single(plainEntries, e => e.Kind == "rejected");
        Assert.False(plainRejected.Fields.ContainsKey("diag_scope"));
    }

    [Fact]
    public void RequestLockEnterAndReturnRecordBothHResults()
    {
        var (store, sink, entries) = NewStore(traceEnabled: true);
        sink.LockGranted = _ => TsfHResult.S_OK;

        var hr = store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out var sessionHr);

        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(TsfHResult.S_OK, sessionHr);
        var enter = Assert.Single(entries, e => e.Kind == "request-lock-enter");
        var returned = Assert.Single(entries, e => e.Kind == "request-lock-return");
        Assert.Equal(enter.Fields["diag_lock_request_id"], returned.Fields["diag_lock_request_id"]);
        Assert.Equal("S_OK", returned.Fields["hresult"]);
        Assert.Equal("S_OK", returned.Fields["session_hresult"]);
        // 返回后锁请求标签已恢复。
        Assert.Equal(0, store.Chain.CurrentLockRequestId);
    }

    [Fact]
    public void RejectedLockRequestIsRecordedWithSessionHResult()
    {
        var (store, _, entries) = NewStore(traceEnabled: true);

        // 只有写位、没有读位：白名单拒绝，*phrSession = E_INVALIDARG。
        var hr = store.RequestLock(TsfFlags.TS_LF_WRITE, out var sessionHr);

        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(TsfHResult.E_INVALIDARG, sessionHr);
        var returned = Assert.Single(entries, e => e.Kind == "request-lock-return");
        Assert.Equal("E_INVALIDARG", returned.Fields["session_hresult"]);
    }

    [Fact]
    public void ProbeScopesAreMarkedOnlyInsideApplicationCalls()
    {
        // 归因常量本身是明确的：只有应用探针三阶段，其余为回调/未知。
        Assert.Equal("request-session", LayoutChainScope.RequestSession);
        Assert.Equal("view-query", LayoutChainScope.ViewQuery);
        Assert.Equal("store-direct-query", LayoutChainScope.StoreDirectQuery);
        Assert.Equal("tsf-callback", LayoutChainScope.TsfCallback);
        Assert.Equal("unknown", LayoutChainScope.Unknown);

        var chain = new LayoutChainTrace(true);
        // 不在任何作用域内时绝不能是 probe 值。
        Assert.DoesNotContain(chain.DescribeScope(),
            new[] { LayoutChainScope.RequestSession, LayoutChainScope.ViewQuery, LayoutChainScope.StoreDirectQuery });
    }
}
