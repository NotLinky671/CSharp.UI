using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 无 GUI 自检：只驱动纯托管文档、锁、通知、几何、归属与清理链，不创建窗口、不初始化 COM、
/// 不发送任何输入。正式覆盖在 tests/CSharp.UI.TsfHostCheck.Tests。
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static int _failed;

    internal static int Run()
    {
        _passed = 0;
        _failed = 0;

        RunDocumentChecks();
        RunLockChecks();
        RunNotificationChecks();
        RunGeometryChecks();
        RunAttributionChecks();
        RunCleanupChecks();
        RunIdentityChecks();
        RunHostStartupChecks();
        RunContextViewProbeChecks();
        RunVisibilityOpChecks();
        RunTsfFocusOpChecks();

        Console.WriteLine($"selftest: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunDocumentChecks()
    {
        var doc = new AcpTextDocument();
        doc.Reset("😀");
        Check("document: surrogate pair is two ACP units", doc.Length == 2, $"length={doc.Length}");
        Check("document: high surrogate readable", doc.GetCodeUnit(0) == '\uD83D', $"u+{(int)doc.GetCodeUnit(0):X4}");
        Check("document: low surrogate readable", doc.GetCodeUnit(1) == '\uDE00', $"u+{(int)doc.GetCodeUnit(1):X4}");
        Check("document: half-pair GetText", doc.GetText(0, 1) == "\uD83D");

        doc.Reset("ab");
        var before = doc.Version;
        var replaced = doc.TryReplace(1, 1, "X", AcpEditOrigin.App, out var change);
        Check("document: insert in middle", replaced && doc.Text == "aXb", doc.Text);
        Check("document: version increments", doc.Version > before, $"{before}->{doc.Version}");
        Check("document: change range", change.Start == 1 && change.OldEnd == 1 && change.NewEnd == 2,
            $"{change.Start},{change.OldEnd},{change.NewEnd}");
        Check("document: invalid range rejected", !doc.TryReplace(3, 4, "z", AcpEditOrigin.App, out _));
        Check("document: selection clamped on replace",
            doc.TrySetSelection(0, 3) && doc.TryReplace(0, 3, "q", AcpEditOrigin.App, out _)
            && doc.Selection == (0, 1), $"{doc.Selection}");
    }

    private static void RunLockChecks()
    {
        var trace = NoTrace();
        var store = new AcpTextStore(trace);
        var sink = new RecordingTextStoreSink();
        Check("lock: no sink -> phrSession E_FAIL",
            store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out var noSinkHr) == 0
            && noSinkHr == TsfHResult.E_FAIL, TsfHResult.Describe(noSinkHr));

        store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS);

        var syncRequest = store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out var syncHr);
        Check("lock: sync grant", syncRequest == 0 && syncHr == 0 && sink.LockGrantedCount == 1,
            $"{TsfHResult.Describe(syncHr)} count={sink.LockGrantedCount}");

        var nestedHr = 0;
        sink.LockGranted = _ =>
        {
            store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out nestedHr);
            return 0;
        };
        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        sink.LockGranted = null;
        Check("lock: nested sync while held -> TS_E_SYNCHRONOUS",
            nestedHr == TsfHResult.TS_E_SYNCHRONOUS, TsfHResult.Describe(nestedHr));

        var asyncHr = 0;
        var grants = 0;
        sink.LockGranted = flags =>
        {
            grants++;
            if (grants == 1)
            {
                store.RequestLock(TsfFlags.TS_LF_READ, out asyncHr);
            }

            return 0;
        };
        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        sink.LockGranted = null;
        Check("lock: async queued -> TS_S_ASYNC", asyncHr == TsfHResult.TS_S_ASYNC, TsfHResult.Describe(asyncHr));
        Check("lock: queued async granted after release", grants == 2, $"grants={grants}");
        Check("lock: queue drained", store.Locks.PendingCount == 0, $"pending={store.Locks.PendingCount}");

        var invalidStore = new AcpTextStore(NoTrace());
        var invalidSink = new RecordingTextStoreSink();
        invalidStore.AdviseSink(TsfIids.TextStoreAcpSink, invalidSink, TsfFlags.TS_AS_ALL_SINKS);
        var invalidGrants = 0;
        invalidSink.LockGranted = _ =>
        {
            invalidGrants++;
            return TsfHResult.S_OK;
        };
        invalidStore.RequestLock(TsfFlags.TS_LF_READ | 0x100, out var unknownHr);
        invalidStore.RequestLock(0x4, out var writeOnlyHr);
        Check("lock: unknown/write-only flags rejected",
            unknownHr == TsfHResult.E_INVALIDARG && writeOnlyHr == TsfHResult.E_INVALIDARG
            && invalidGrants == 0,
            $"unknown={TsfHResult.Describe(unknownHr)} writeOnly={TsfHResult.Describe(writeOnlyHr)} grants={invalidGrants}");

        var upgradeStore = new AcpTextStore(NoTrace());
        var upgradeSink = new RecordingTextStoreSink();
        upgradeStore.AdviseSink(TsfIids.TextStoreAcpSink, upgradeSink, TsfFlags.TS_AS_ALL_SINKS);
        var upgradeGrants = 0;
        upgradeSink.LockGranted = flags =>
        {
            upgradeGrants++;
            if (upgradeGrants == 1)
            {
                upgradeStore.RequestLock(TsfFlags.TS_LF_READ, out _);
                upgradeStore.RequestLock(TsfFlags.TS_LF_READWRITE, out _);
            }

            return TsfHResult.S_OK;
        };
        upgradeStore.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);
        Check("lock: read->write upgrade coalesced",
            upgradeGrants == 2 && upgradeSink.LastLockFlags == TsfFlags.TS_LF_READWRITE
            && upgradeStore.Locks.PendingCount == 0,
            $"grants={upgradeGrants} last=0x{upgradeSink.LastLockFlags:X} pending={upgradeStore.Locks.PendingCount}");

        var drainStore = new AcpTextStore(NoTrace());
        var drainSink = new RecordingTextStoreSink();
        drainStore.AdviseSink(TsfIids.TextStoreAcpSink, drainSink, TsfFlags.TS_AS_ALL_SINKS);
        var drainPosts = new Queue<Action>();
        drainStore.NotificationDispatcher = drainPosts.Enqueue;
        var drainGrants = 0;
        drainSink.LockGranted = flags =>
        {
            drainGrants++;
            if (drainGrants <= 20)
            {
                drainStore.RequestLock(TsfFlags.TS_LF_READWRITE, out _);
            }

            return TsfHResult.S_OK;
        };
        drainStore.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);
        Check("lock: drain limit posts continuation",
            drainGrants == 17 && drainStore.Locks.PendingCount == 1 && drainPosts.Count == 1,
            $"grants={drainGrants} pending={drainStore.Locks.PendingCount} posts={drainPosts.Count}");
        while (drainPosts.Count > 0)
        {
            drainPosts.Dequeue()();
        }
        Check("lock: continuation grants promised upgrade",
            drainGrants == 21 && drainStore.Locks.PendingCount == 0 && drainPosts.Count == 0,
            $"grants={drainGrants} pending={drainStore.Locks.PendingCount} posts={drainPosts.Count}");
    }

    private static void RunNotificationChecks()
    {
        var store = new AcpTextStore(NoTrace());
        var sink = new RecordingTextStoreSink();
        store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_TEXT_CHANGE);

        var textHr = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => textHr = store.ApplyAppText("abc"), out _, out _);
        Check("notify: app text change delivered", textHr == 0 && sink.TextChangeCount == 1,
            $"{TsfHResult.Describe(textHr)} count={sink.TextChangeCount}");
        Check("notify: selection masked out", sink.SelectionChangeCount == 0);

        var tsfTextHr = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => tsfTextHr = SetText(store, 3, 3, "d"), out _, out _);
        Check("notify: TSF-origin SetText does not echo OnTextChange",
            tsfTextHr == 0 && sink.TextChangeCount == 1, $"count={sink.TextChangeCount}");

        var tsfSelectionHr = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE, () =>
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TsSelectionAcp>());
            try
            {
                Marshal.StructureToPtr(new TsSelectionAcp { AcpStart = 1, AcpEnd = 1 }, pointer, false);
                tsfSelectionHr = store.SetSelection(1, pointer);
                return tsfSelectionHr;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }, out _, out _);
        Check("notify: TSF-origin SetSelection does not echo",
            tsfSelectionHr == 0 && sink.SelectionChangeCount == 0, $"count={sink.SelectionChangeCount}");

        var layout = AcpLayoutSnapshot.Build(
            store.LayoutGeneration, store.Document.Version, store.Document.Text,
            new[] { 10, 10, 10, 10 }, 20, TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 0);
        store.PublishLayout(layout, notify: true);
        Check("notify: layout masked out", sink.LayoutChangeCount == 0,
            $"count={sink.LayoutChangeCount}");

        var store2 = new AcpTextStore(NoTrace());
        var sink2 = new RecordingTextStoreSink();
        store2.AdviseSink(TsfIids.TextStoreAcpSink, sink2, TsfFlags.TS_AS_LAYOUT_CHANGE);
        store2.PublishLayout(AcpLayoutSnapshot.Build(
            store2.LayoutGeneration, store2.Document.Version, "", Array.Empty<int>(), 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 0), notify: true);
        Check("notify: layout delivered when masked in", sink2.LayoutChangeCount == 2,
            $"count={sink2.LayoutChangeCount}");
        Check("notify: layout create before change",
            sink2.LayoutChanges.Count >= 2
            && sink2.LayoutChanges[0].Code == TsfFlags.TS_LC_CREATE
            && sink2.LayoutChanges[1].Code == TsfFlags.TS_LC_CHANGE,
            $"codes={string.Join(",", sink2.LayoutChanges.Select(c => c.Code))}");

        store2.UnadviseSink(sink2);
        store2.PublishLayout(AcpLayoutSnapshot.Build(
            store2.LayoutGeneration, store2.Document.Version, "", Array.Empty<int>(), 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 0), notify: true);
        Check("notify: unadvised sink gets nothing", sink2.LayoutChangeCount == 2,
            $"count={sink2.LayoutChangeCount}");

        // RequestLock 返回后由宿主安全阶段投递；调用栈内不得发送。
        var deferredStore = new AcpTextStore(NoTrace());
        var deferredSink = new RecordingTextStoreSink();
        deferredStore.AdviseSink(TsfIids.TextStoreAcpSink, deferredSink, TsfFlags.TS_AS_ALL_SINKS);
        var posts = new Queue<Action>();
        deferredStore.NotificationDispatcher = posts.Enqueue;
        deferredStore.ResetDocument("");
        deferredSink.LockGranted = _ => deferredStore.ApplyAppText("x");
        deferredStore.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        Check("notify: deferred until after RequestLock returns",
            deferredSink.TextChangeCount == 0 && posts.Count == 1 && deferredStore.Locks.LockType == 0,
            $"count={deferredSink.TextChangeCount} posts={posts.Count} lock=0x{deferredStore.Locks.LockType:X}");
        posts.Dequeue()();
        Check("notify: delivered in safe phase", deferredSink.TextChangeCount == 1,
            $"count={deferredSink.TextChangeCount}");

        var noDispatcherStore = new AcpTextStore(NoTrace());
        var noDispatcherSink = new RecordingTextStoreSink();
        noDispatcherStore.AdviseSink(TsfIids.TextStoreAcpSink, noDispatcherSink, TsfFlags.TS_AS_ALL_SINKS);
        noDispatcherStore.ResetDocument("");
        noDispatcherSink.LockGranted = _ => noDispatcherStore.ApplyAppText("y");
        noDispatcherStore.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        Check("notify: no dispatcher keeps pending",
            noDispatcherSink.TextChangeCount == 0,
            $"count={noDispatcherSink.TextChangeCount}");
        noDispatcherStore.FlushDeferredNotifications();
        Check("notify: manual same-thread flush delivers",
            noDispatcherSink.TextChangeCount == 1,
            $"count={noDispatcherSink.TextChangeCount}");
    }

    private static void RunGeometryChecks()
    {
        var store = new AcpTextStore(NoTrace());
        var sink = new RecordingTextStoreSink();
        store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS);
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => SetText(store, 0, 0, "ab"), out _, out _);

        store.PublishLayout(AcpLayoutSnapshot.Build(
            store.LayoutGeneration, store.Document.Version, "ab", new[] { 10, 10 }, 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 0), notify: true);

        var textExtHr = 0;
        var rect = default(TsfRect);
        var clipped = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            textExtHr = store.GetTextExt(1, 0, 1, out rect, out clipped);
            return textExtHr;
        }, out _, out _);
        Check("geometry: fresh range", textExtHr == 0 && rect.Left == 0 && rect.Right == 10 && clipped == 0,
            $"{TsfHResult.Describe(textExtHr)} {rect} clipped={clipped}");

        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => SetText(store, 2, 2, "c"), out _, out _);
        var staleHr = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            staleHr = store.GetTextExt(1, 0, 1, out _, out _);
            return staleHr;
        }, out _, out _);
        Check("geometry: stale after text change -> TS_E_NOLAYOUT", staleHr == TsfHResult.TS_E_NOLAYOUT,
            TsfHResult.Describe(staleHr));

        store.PublishLayout(AcpLayoutSnapshot.Build(
            store.LayoutGeneration, store.Document.Version, store.Document.Text, new[] { 10, 10, 10 }, 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 0), notify: true);
        store.InvalidateLayout("move");
        var invalidatedHr = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            invalidatedHr = store.GetTextExt(1, 0, 1, out _, out _);
            return invalidatedHr;
        }, out _, out _);
        Check("geometry: invalidated -> TS_E_NOLAYOUT", invalidatedHr == TsfHResult.TS_E_NOLAYOUT,
            TsfHResult.Describe(invalidatedHr));

        store.PublishLayout(AcpLayoutSnapshot.Build(
            store.LayoutGeneration, store.Document.Version, store.Document.Text, new[] { 10, 10, 10 }, 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 50), notify: true);
        var clippedHr = 0;
        var clippedRect = default(TsfRect);
        var clippedFlag = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            clippedHr = store.GetTextExt(1, 0, 1, out clippedRect, out clippedFlag);
            return clippedHr;
        }, out _, out _);
        Check("geometry: fully invisible -> S_OK + zero rect",
            clippedHr == 0 && clippedRect.IsEmpty && clippedFlag == 1,
            $"{TsfHResult.Describe(clippedHr)} rect={clippedRect} clipped={clippedFlag}");

        store.PublishLayout(AcpLayoutSnapshot.Build(
            store.LayoutGeneration, store.Document.Version, store.Document.Text, new[] { 10, 10, 10 }, 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 10), notify: true);
        var partialHr = 0;
        var partialClipped = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            partialHr = store.GetTextExt(1, 0, 1, out _, out partialClipped);
            return partialHr;
        }, out _, out _);
        Check("geometry: partially clipped flag", partialHr == 0 && partialClipped == 1,
            $"{TsfHResult.Describe(partialHr)} clipped={partialClipped}");

        var point = new TsfPoint { X = 15, Y = 5 };
        var pointPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TsfPoint>());
        try
        {
            Marshal.StructureToPtr(point, pointPtr, false);
            var pointHr = 0;
            var acp = 0;
            StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
            {
                pointHr = store.GetACPFromPoint(1, pointPtr, 0, out acp);
                return pointHr;
            }, out _, out _);
            Check("geometry: hit test", pointHr == 0 && acp == 1, $"{TsfHResult.Describe(pointHr)} acp={acp}");
        }
        finally
        {
            Marshal.FreeHGlobal(pointPtr);
        }

        // run-only GetText（pchPlain=NULL,cchPlainReq=0）必须返回 run 并推进 pacpNext。
        var runPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TsRunInfo>());
        try
        {
            var runHr = 0;
            var plainRet = 99u;
            var runRet = 0u;
            var next = 0;
            StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
            {
                runHr = store.GetText(0, -1, IntPtr.Zero, 0, out plainRet, runPtr, 1, out runRet, out next);
                return runHr;
            }, out _, out _);
            var run = Marshal.PtrToStructure<TsRunInfo>(runPtr);
            Check("geometry: run-only GetText advances",
                runHr == 0 && plainRet == 0 && runRet == 1 && run.Count == 3 && next == 3,
                $"{TsfHResult.Describe(runHr)} plain={plainRet} runs={runRet} count={run.Count} next={next}");
        }
        finally
        {
            Marshal.FreeHGlobal(runPtr);
        }

        // R24：非法 view 按官方样例返回 E_INVALIDARG，且不再要求读锁（这里仍在锁内执行以验证一致）。
        var badViewHr = 0;
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            badViewHr = store.GetScreenExt(99, out _);
            return badViewHr;
        }, out _, out _);
        Check("geometry: invalid view is E_INVALIDARG", badViewHr == TsfHResult.E_INVALIDARG,
            TsfHResult.Describe(badViewHr));

        // R24：显示表面查询不需要文档读锁。
        var noLockScreenHr = store.GetScreenExt(1, out _);
        Check("geometry: GetScreenExt works without a document lock",
            noLockScreenHr != TsfHResult.TS_E_NOLOCK,
            TsfHResult.Describe(noLockScreenHr));

        // 几何拒绝必须留痕：真实 JSONL 不能出现“查询被静默拒绝”的盲区。
        var tracedHResults = new List<string>();
        var tracedStore = new AcpTextStore((stage, kind, fields) =>
        {
            if (stage == "geometry" && kind == "rejected" && fields is not null)
            {
                tracedHResults.Add(fields.TryGetValue("hresult", out var value)
                    ? value?.ToString() ?? "(null)"
                    : "(missing)");
            }
        });
        var noLockHr = tracedStore.GetTextExt(1, 0, 0, out _, out _);
        Check("geometry: rejected query is traced with HRESULT",
            noLockHr == TsfHResult.TS_E_NOLOCK && tracedHResults.Count == 1
            && tracedHResults[0] == "TS_E_NOLOCK",
            $"{TsfHResult.Describe(noLockHr)} traces={string.Join(",", tracedHResults)}");
    }

    private static void RunAttributionChecks()
    {
        var store = new AcpTextStore(NoTrace());
        var sink = new RecordingTextStoreSink();
        store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS);
        var recorder = new BindingRecorder(store, NoTrace());

        store.ResetDocument("");
        store.BeginComposition(0, 0);
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE, () =>
        {
            var hr = InsertText(store, "ni");
            if (hr < 0)
            {
                return hr;
            }

            return SetText(store, 0, 2, "你好");
        }, out _, out _);
        var committed = store.EndComposition();
        Check("attribution: composition commits once",
            committed == "你好" && recorder.Writes == 1 && store.Document.Text == "你好",
            $"committed={committed} writes={recorder.Writes} text={store.Document.Text}");

        store.EndComposition();
        Check("attribution: repeated end is idempotent", recorder.Writes == 1, $"writes={recorder.Writes}");

        store.ResetDocument("");
        store.BeginComposition(0, 0);
        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE, () =>
        {
            var hr = InsertText(store, "ni");
            if (hr < 0)
            {
                return hr;
            }

            return SetText(store, 0, 2, "");
        }, out _, out _);
        store.EndComposition();
        Check("attribution: cancel writes nothing", recorder.Writes == 1, $"writes={recorder.Writes}");

        StoreTestHarness.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("a"), out _, out _);
        Check("attribution: app character writes once", recorder.Writes == 2, $"writes={recorder.Writes}");

        // 初始空文档连续插入必须正序，caret 折叠到新文本末尾。
        var orderStore = new AcpTextStore(NoTrace());
        var orderSink = new RecordingTextStoreSink();
        orderStore.AdviseSink(TsfIids.TextStoreAcpSink, orderSink, TsfFlags.TS_AS_ALL_SINKS);
        orderStore.ResetDocument("");
        StoreTestHarness.UnderLock(orderStore, orderSink, TsfFlags.TS_LF_READWRITE,
            () => orderStore.ApplyAppText("a"), out _, out _);
        StoreTestHarness.UnderLock(orderStore, orderSink, TsfFlags.TS_LF_READWRITE,
            () => orderStore.ApplyAppText("b"), out _, out _);
        Check("attribution: initial insertion advances caret",
            orderStore.Document.Text == "ab" && orderStore.Document.Selection == (2, 2),
            $"text={orderStore.Document.Text} selection={orderStore.Document.Selection}");

        StoreTestHarness.UnderLock(orderStore, orderSink, TsfFlags.TS_LF_READWRITE,
            () => orderStore.ApplyAppSelection(1, 1), out _, out _);
        StoreTestHarness.UnderLock(orderStore, orderSink, TsfFlags.TS_LF_READWRITE,
            () => orderStore.ApplyAppText("X"), out _, out _);
        Check("attribution: mid-document insertion advances caret",
            orderStore.Document.Text == "aXb" && orderStore.Document.Selection == (2, 2),
            $"text={orderStore.Document.Text} selection={orderStore.Document.Selection}");

        // CreateContext owner 同时暴露 ITfContextOwnerCompositionSink 并真实委托。
        var ownerStore = new AcpTextStore(NoTrace());
        var ownerSink = new RecordingCompositionSink();
        ownerStore.CompositionObserver = ownerSink;
        var owner = (ITfContextOwnerCompositionSink)ownerStore;
        var ownerHr = owner.OnStartComposition(null!, out var pfOk);
        owner.OnUpdateComposition(null!, IntPtr.Zero);
        owner.OnEndComposition(null!);
        Check("composition: owner exposes and delegates sink",
            ownerHr == 0 && pfOk == 1 && ownerSink.StartCount == 1
            && ownerSink.UpdateCount == 1 && ownerSink.EndCount == 1,
            $"{TsfHResult.Describe(ownerHr)} pfOk={pfOk} start={ownerSink.StartCount} update={ownerSink.UpdateCount} end={ownerSink.EndCount}");
    }

    private static void RunCleanupChecks()
    {
        var order = new List<string>();
        var chain = new CleanupChain();
        chain.Add("first", () => TsfHResult.S_OK);
        chain.Add("fails", () => TsfHResult.E_FAIL);
        chain.Add("throws", () => throw new InvalidOperationException("injected"));
        chain.Add("last", () => TsfHResult.S_OK);
        var report = chain.Run();
        Check("cleanup: continues after failures", report.Steps.Count == 4 && report.Failures == 2,
            $"steps={report.Steps.Count} failures={report.Failures}");
        Check("cleanup: later step executed", report.Steps[^1].Name == "last");

        var store = new AcpTextStore(NoTrace());
        var host = new TsfSessionHost(store, IntPtr.Zero, null, NoTrace(), sta: false, () => { });
        host.Cleanup();
        host.Cleanup();
        Check("cleanup: idempotent without engagement", host.CleanupFailures == 0,
            $"failures={host.CleanupFailures}");
        _ = order;
    }

    private static void RunIdentityChecks()
    {
        Check("identity: Microsoft Pinyin profile matches",
            ImeIdentity.IsMicrosoftPinyin(0, ImeProbe.TsfProfile.TfProfileTypeInputProcessor,
                ImeProbe.TsfProfile.LangIdChineseSimplified,
                ImeProbe.TsfProfile.MicrosoftPinyinClsid, ImeProbe.TsfProfile.MicrosoftPinyinProfile));

        Check("identity: other profile guid is rejected",
            !ImeIdentity.IsMicrosoftPinyin(0, ImeProbe.TsfProfile.TfProfileTypeInputProcessor,
                ImeProbe.TsfProfile.LangIdChineseSimplified,
                ImeProbe.TsfProfile.MicrosoftPinyinClsid, Guid.NewGuid()));

        Check("identity: wrong langid is rejected",
            !ImeIdentity.IsMicrosoftPinyin(0, ImeProbe.TsfProfile.TfProfileTypeInputProcessor,
                0x0409,
                ImeProbe.TsfProfile.MicrosoftPinyinClsid, ImeProbe.TsfProfile.MicrosoftPinyinProfile));
    }

    /// <summary>
    /// R17 的纯策略自检：宿主套间默认 MTA、STA 仅在显式请求时使用；
    /// 只有 S_OK/S_FALSE 才拥有需要成对释放的 COM 引用；真实套间必须匹配请求。
    /// 不在这里初始化 COM（真实初始化由 HostApartment 在宿主线程上完成，见 --hidden-host）。
    /// </summary>
    private static void RunHostStartupChecks()
    {
        Check("host: MTA is the default CoInitializeEx flag",
            HostApartment.CoInitFlag(false) == ComNative.CoInitMultithreaded
            && HostApartment.CoInitFlag(true) == ComNative.CoInitApartmentThreaded,
            $"mta=0x{HostApartment.CoInitFlag(false):X} sta=0x{HostApartment.CoInitFlag(true):X}");

        Check("host: MTA keeps the entry thread, STA self-selects a dedicated thread",
            HostApartment.SelectHostThread(false) == HostApartment.HostThreadSelection.ProcessEntry
            && HostApartment.SelectHostThread(true) == HostApartment.HostThreadSelection.DedicatedSta,
            $"mta={HostApartment.SelectHostThread(false)} sta={HostApartment.SelectHostThread(true)}");

        Check("host: only S_OK/S_FALSE own a COM reference",
            HostApartment.OwnsReferenceFor(TsfHResult.S_OK)
            && HostApartment.OwnsReferenceFor(TsfHResult.S_FALSE)
            && !HostApartment.OwnsReferenceFor(TsfHResult.RPC_E_CHANGED_MODE)
            && !HostApartment.OwnsReferenceFor(TsfHResult.CO_E_NOTINITIALIZED)
            && !HostApartment.OwnsReferenceFor(TsfHResult.E_FAIL),
            $"changed_mode={HostApartment.OwnsReferenceFor(TsfHResult.RPC_E_CHANGED_MODE)}");

        Check("host: requested apartment must be the real one",
            HostApartment.MatchesExpectedApartment(false, HostApartment.AptTypeMta)
            && HostApartment.MatchesExpectedApartment(true, HostApartment.AptTypeSta)
            && HostApartment.MatchesExpectedApartment(true, HostApartment.AptTypeMainSta)
            && !HostApartment.MatchesExpectedApartment(true, HostApartment.AptTypeMta)
            && !HostApartment.MatchesExpectedApartment(false, HostApartment.AptTypeSta)
            && !HostApartment.MatchesExpectedApartment(true, HostApartment.AptTypeNa),
            $"sta_on_mta={HostApartment.MatchesExpectedApartment(true, HostApartment.AptTypeMta)}");

        var hiddenOk = Program.TryParse(new[] { "--hidden-host" }, out var hiddenParsed, out _);
        Check("host: --hidden-host defaults to MTA and hidden mode",
            hiddenOk && hiddenParsed.Options is { HiddenHost: true, Sta: false },
            $"ok={hiddenOk} hidden={hiddenParsed.Options?.HiddenHost} sta={hiddenParsed.Options?.Sta}");

        var staOk = Program.TryParse(new[] { "--hidden-host", "--apartment", "sta" },
            out var staParsed, out _);
        Check("host: --hidden-host accepts explicit STA",
            staOk && staParsed.Options is { HiddenHost: true, Sta: true },
            $"ok={staOk} hidden={staParsed.Options?.HiddenHost} sta={staParsed.Options?.Sta}");

        var conflict = Program.TryParse(new[] { "--hidden-host", "--scenario", "scroll-follow" },
            out _, out var conflictError);
        Check("host: --hidden-host rejects --scenario instead of silently ignoring it",
            !conflict && conflictError.Contains("hidden-host", StringComparison.Ordinal),
            conflictError);
    }

    /// <summary>
    /// context/view 只读几何对照探针的纯策略自检：只读会话标志、临时范围选择、状态分类、
    /// 默认关闭不触碰 context，以及缺少 context 时如实报告且不重试。不初始化 COM。
    /// </summary>
    private static void RunContextViewProbeChecks()
    {
        Check("context-view: read-only session flags are SYNC|READ only",
            ContextViewProbe.ReadOnlySessionFlags == (TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READ)
            && (ContextViewProbe.ReadOnlySessionFlags & 0x4) == 0
            && (ContextViewProbe.ReadOnlySessionFlags & TsfFlags.TF_ES_READWRITE) != TsfFlags.TF_ES_READWRITE
            && (ContextViewProbe.ReadOnlySessionFlags & TsfFlags.TF_ES_ASYNC) == 0,
            $"flags=0x{ContextViewProbe.ReadOnlySessionFlags:X}");

        var composition = ContextViewProbe.ResolveRange(true, 2, 5, 5);
        var caret = ContextViewProbe.ResolveRange(false, 0, 0, 7);
        var emptyComposition = ContextViewProbe.ResolveRange(true, 3, 3, 3);
        var clamped = ContextViewProbe.ResolveRange(false, 0, 0, -4);
        Check("context-view: range prefers composition span, else caret",
            composition == new ContextViewRange(2, 5, "composition")
            && caret == new ContextViewRange(7, 7, "caret")
            && emptyComposition == new ContextViewRange(3, 3, "composition-empty")
            && clamped == new ContextViewRange(0, 0, "caret"),
            $"{composition} {caret} {emptyComposition} {clamped}");

        var rangeOk = ContextViewProbe.RangeMatches(new ContextViewRange(2, 5, "composition"), 2, 3)
            && !ContextViewProbe.RangeMatches(new ContextViewRange(2, 5, "composition"), 2, 2)
            && !ContextViewProbe.RangeMatches(new ContextViewRange(2, 5, "composition"), 3, 3)
            && ContextViewProbe.RangeMatches(new ContextViewRange(4, 4, "caret"), 4, 0)
            && !ContextViewProbe.RangeMatches(new ContextViewRange(4, 4, "caret"), 5, 0);
        Check("context-view: range match requires same start and same length", rangeOk,
            $"ok={rangeOk}");

        Check("context-view: status classification preserves refusals",
            ContextViewProbe.Classify(TsfHResult.E_FAIL, false, 0, false, 0, false, false, false)
                == ContextViewProbeStatus.RequestFailed
            && ContextViewProbe.Classify(0, false, 0, true, 0, true, true, true)
                == ContextViewProbeStatus.SessionNotExecuted
            && ContextViewProbe.Classify(0, true, TsfHResult.E_FAIL, true, 0, true, true, true)
                == ContextViewProbeStatus.SessionFailed
            && ContextViewProbe.Classify(0, true, 0, false, 0, true, true, true)
                == ContextViewProbeStatus.RangeMismatch
            && ContextViewProbe.Classify(0, true, 0, true, TsfHResult.TS_E_NOLAYOUT, true, true, true)
                == ContextViewProbeStatus.Refused
            && ContextViewProbe.Classify(0, true, 0, true, 0, true, true, true)
                == ContextViewProbeStatus.Ok
            && ContextViewProbe.Classify(0, true, 0, true, 0, false, true, true)
                == ContextViewProbeStatus.Mismatch
            && ContextViewProbe.Classify(0, true, 0, true, 0, true, false, true)
                == ContextViewProbeStatus.Mismatch
            && ContextViewProbe.Classify(0, true, 0, true, 0, true, true, false)
                == ContextViewProbeStatus.Mismatch,
            $"{ContextViewProbe.Classify(0, true, 0, true, TsfHResult.TS_E_NOLAYOUT, true, true, true)}");

        // R20 剩余控制流：extent 未知（GetExtent 失败）不能被错判成 range-mismatch。
        Check("context-view: unknown extent is session failure, not range mismatch",
            ContextViewProbe.Classify(0, true, TsfHResult.E_FAIL, null, TsfHResult.E_FAIL,
                false, false, false) == ContextViewProbeStatus.SessionFailed
            && ContextViewProbe.Classify(0, true, 0, null, 0, false, false, false)
                == ContextViewProbeStatus.SessionFailed
            && ContextViewProbe.Classify(0, true, 0, false, 0, false, false, false)
                == ContextViewProbeStatus.RangeMismatch,
            $"{ContextViewProbe.Classify(0, true, 0, null, 0, false, false, false)}");

        var store = new AcpTextStore(NoTrace());
        var disabled = ContextViewProbe.RunIfEnabled(false, "selftest", null, 0, store, NoTrace(),
            new ContextViewRange(0, 0, "caret"));
        Check("context-view: disabled probe does nothing", disabled is null,
            disabled?.ToString() ?? "(null)");

        var unavailable = ContextViewProbe.RunIfEnabled(true, "selftest", null, 0, store, NoTrace(),
            new ContextViewRange(0, 0, "caret"));
        Check("context-view: missing context is reported without retry",
            unavailable is { Status: ContextViewProbeStatus.ContextUnavailable }
            && unavailable.Value.RequestHResult < 0 && !unavailable.Value.RectMatch
            && !unavailable.Value.SessionRan && !unavailable.Value.RangeMatch,
            unavailable?.ToString() ?? "(null)");

        var defaultParse = Program.TryParse(Array.Empty<string>(), out var defaultOptions, out _);
        var enabledParse = Program.TryParse(new[] { "--probe-context-view" }, out var enabledOptions, out _);
        Check("context-view: CLI default off, explicit switch on",
            defaultParse && defaultOptions.Options is { ProbeContextView: false }
            && enabledParse && enabledOptions.Options is { ProbeContextView: true },
            $"default={defaultOptions.Options?.ProbeContextView} enabled={enabledOptions.Options?.ProbeContextView}");

        RunLayoutChainChecks();
    }

    /// <summary>
    /// 布局调用链诊断（`--trace-layout-chain`）纯策略自检：默认关闭、配对 ID、作用域恢复与归因常量。
    /// 不初始化 COM、不创建窗口、不查询原生焦点。
    /// </summary>
    private static void RunLayoutChainChecks()
    {
        var disabled = LayoutChainTrace.Disabled;
        Check("layout-chain: disabled chain reports unknown and adds nothing",
            !disabled.Enabled
            && disabled.DescribeScope() == LayoutChainScope.Unknown
            && disabled.CurrentNotificationId == 0
            && disabled.Enrich(null) is null,
            $"enabled={disabled.Enabled}");

        var enabled = new LayoutChainTrace(true);
        var id = enabled.BeginNotification();
        enabled.EnterNotification(id);
        var inCallback = enabled.DescribeScope();
        var callbackId = enabled.CurrentNotificationId;
        enabled.ExitNotification();
        var afterExit = enabled.DescribeScope();
        var afterId = enabled.CurrentNotificationId;
        Check("layout-chain: notification id pairs and clears on exit",
            inCallback == LayoutChainScope.TsfCallback && callbackId == id
            && afterExit == LayoutChainScope.Unknown && afterId == 0,
            $"in={inCallback} id={callbackId} after={afterExit}");

        string afterScope;
        using (enabled.PushScope(LayoutChainScope.RequestSession))
        {
            try
            {
                using (enabled.PushScope(LayoutChainScope.ViewQuery))
                {
                    throw new InvalidOperationException("boom");
                }
            }
            catch (InvalidOperationException)
            {
                // 内层已恢复为 request-session。
            }

            afterScope = enabled.DescribeScope();
        }

        Check("layout-chain: application scope restores after nested exception",
            afterScope == LayoutChainScope.RequestSession
            && enabled.DescribeScope() == LayoutChainScope.Unknown,
            $"after_nested={afterScope} final={enabled.DescribeScope()}");

        var fields = enabled.Enrich(new Dictionary<string, object?> { ["method"] = "GetTextExt" });
        Check("layout-chain: enrich keeps old fields and adds attribution",
            fields is not null && fields.ContainsKey("method")
            && fields.ContainsKey("diag_scope")
            && fields["diag_scope"] is LayoutChainScope.Unknown,
            fields is null ? "(null)" : $"scope={fields["diag_scope"]}");

        // R23：通知/锁请求必须为嵌套栈，内层退出恢复外层而不是清零。
        var nested = new LayoutChainTrace(true);
        var outerNotification = nested.BeginNotification();
        nested.EnterNotification(outerNotification);
        var innerNotification = nested.BeginNotification();
        nested.EnterNotification(innerNotification);
        nested.ExitNotification();
        var restoredNotification = nested.CurrentNotificationId;
        nested.ExitNotification();
        var clearedNotification = nested.CurrentNotificationId;

        var outerLock = nested.BeginLockRequest();
        nested.EnterLockRequest(outerLock);
        var innerLock = nested.BeginLockRequest();
        nested.EnterLockRequest(innerLock);
        nested.ExitLockRequest();
        var restoredLock = nested.CurrentLockRequestId;
        nested.ExitLockRequest();
        var clearedLock = nested.CurrentLockRequestId;

        Check("layout-chain: nested notification/lock exits restore outer IDs",
            restoredNotification == outerNotification && clearedNotification == 0
            && restoredLock == outerLock && clearedLock == 0,
            $"notification {innerNotification}->{restoredNotification}->{clearedNotification}, lock {innerLock}->{restoredLock}->{clearedLock}");

        // R23：状态按真实 OS 线程隔离；工作项线程读到的是它自己的空栈。
        var isolated = new LayoutChainTrace(true);
        var mainThreadId = Environment.CurrentManagedThreadId;
        using (isolated.PushScope(LayoutChainScope.RequestSession))
        {
            var done = new ManualResetEventSlim(false);
            var seen = "unset";
            var otherThread = false;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                seen = isolated.DescribeScope();
                otherThread = Environment.CurrentManagedThreadId != mainThreadId;
                done.Set();
            }, null);

            var waited = done.Wait(TimeSpan.FromSeconds(5));
            Check("layout-chain: app scope does not leak to another thread",
                waited && otherThread && seen == LayoutChainScope.Unknown,
                $"waited={waited} other_thread={otherThread} seen={seen}");
        }

        // R23：日志委托抛异常不得穿透 FocusSnapshot.Capture。
        var loggerException = false;
        try
        {
            FocusSnapshot.Capture(new LayoutChainTrace(true),
                (_, _, _) => throw new InvalidOperationException("selftest-logger"),
                "selftest", IntPtr.Zero, null, null, null);
        }
        catch (Exception)
        {
            loggerException = true;
        }

        Check("layout-chain: throwing logger does not escape focus snapshot", !loggerException,
            $"threw={loggerException}");

        RunUiElementActivationChecks();
    }

    /// <summary>
    /// R26：ActivateEx 模式的纯逻辑自检——参数默认关闭、ABI 形状与“不伪造激活”。
    /// 不启动 COM、不调用 TSF；真实 flags 由 Codex 原生运行记录。
    /// </summary>
    private static void RunUiElementActivationChecks()
    {
        var defaultParse = Program.TryParse(Array.Empty<string>(), out var defaultOptions, out _);
        var enabledParse = Program.TryParse(new[] { "--uielement-enabled-only" },
            out var enabledOptions, out _);
        Check("uielement: CLI default off, explicit switch on",
            defaultParse && defaultOptions.Options is { UiElementEnabledOnly: false }
            && enabledParse && enabledOptions.Options is { UiElementEnabledOnly: true },
            $"default={defaultOptions.Options?.UiElementEnabledOnly} "
            + $"enabled={enabledOptions.Options?.UiElementEnabledOnly}");

        Check("uielement: switch does not enable other diagnostics",
            enabledOptions.Options is { ProbeContextView: false, TraceLayoutChain: false,
                SuppressCandidateUi: false },
            $"probe={enabledOptions.Options?.ProbeContextView} "
            + $"trace={enabledOptions.Options?.TraceLayoutChain} "
            + $"suppress={enabledOptions.Options?.SuppressCandidateUi}");

        var baseCount = typeof(ITfThreadMgr).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly).Length;
        var declared = typeof(ITfThreadMgrEx).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly);
        // ComImport 基接口列表不影响 vtable：必须显式展平 11 个基方法 + 2 个扩展 = 13，
        // 末槽必须是 15（ActivateEx=14、GetActiveFlags=15），否则会错调到 Activate/Deactivate。
        Check("uielement: ITfThreadMgrEx ABI (13 flattened methods, end slot 15)",
            baseCount == 11 && declared.Length == 13
            && declared[11].Name == "ActivateEx" && declared[12].Name == "GetActiveFlags"
            && System.Runtime.InteropServices.Marshal.GetEndComSlot(typeof(ITfThreadMgrEx)) == 15
            && TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY == 0x4
            && TsfIids.ThreadMgrEx == new Guid("3e90ade3-7594-4cb0-bb58-69628f5f458c"),
            $"base={baseCount} declared={declared.Length} "
            + $"end_slot={System.Runtime.InteropServices.Marshal.GetEndComSlot(typeof(ITfThreadMgrEx))} "
            + $"flag=0x{TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY:X}");

        var store = new AcpTextStore(NoTrace());
        var plain = new TsfSessionHost(store, IntPtr.Zero, null, NoTrace(), false, () => { });
        Check("uielement: default session keeps plain activation",
            !plain.UiElementEnabledOnly && plain.ActivationMode == "(not-activated)"
            && plain.ActiveActivationFlags is null,
            $"mode={plain.ActivationMode} flags={plain.ActiveActivationFlags}");

        var explicitMode = new TsfSessionHost(store, IntPtr.Zero, null, NoTrace(), false, () => { },
            uiElementEnabledOnly: true);
        Check("uielement: explicit mode is not a fake activation",
            explicitMode.UiElementEnabledOnly
            && explicitMode.ActivationMode == "(not-activated)"
            && explicitMode.ActiveActivationFlags is null
            && explicitMode.ReadActiveActivationFlags("not-engaged") is null,
            $"mode={explicitMode.ActivationMode} flags={explicitMode.ActiveActivationFlags}");

        // R27：native-ui-lifecycle 场景已注册，且与既有显式探针场景并存。
        Check("uielement: native-ui-lifecycle scenario is registered",
            ScenarioRunner.IsKnownScenario("native-ui-lifecycle")
            && ScenarioRunner.IsKnownScenario("uielement-hide-probe"),
            "native-ui-lifecycle registered alongside uielement-hide-probe");
    }

    private static int SetText(AcpTextStore store, int start, int end, string text)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        try
        {
            return store.SetText(0, start, end, pointer, (uint)text.Length, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>
    /// R30 共享可见性操作核的纯托管自检：归属拒绝/原生 false/原生异常/显示/隐藏/
    /// 原本不可见的恢复语义——全部经 <see cref="ChildSurfaceVisibility.RunSurfaceVisibilityOperation"/>,
    /// 无需窗口。验证控制流与真实调用顺序，不触碰任何 HWND。
    /// </summary>
    private static void RunVisibilityOpChecks()
    {
        var visible = true;
        var setCalls = 0;
        var visibleReads = 0;

        // 成功 hide：owns=true → 读前可见 → set(true->false) → 读后不可见。
        var hide = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: false,
            ownsWindow: () => true,
            isVisible: () => { visibleReads++; return visible; },
            setVisible: v => { setCalls++; visible = v; return true; },
            showOnlyIfWasVisible: true);
        Check("visibility: hide applies and reports was-visible",
            hide.Ok && hide.WasVisible && !hide.VisibleAfter && setCalls == 1,
            $"ok={hide.Ok} was={hide.WasVisible} after={hide.VisibleAfter} calls={setCalls}");

        // 成功 show：操作后可见。
        visibleReads = 0; setCalls = 0;
        var show = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: true,
            ownsWindow: () => true,
            isVisible: () => { visibleReads++; return visible; },
            setVisible: v => { setCalls++; visible = v; return true; },
            showOnlyIfWasVisible: true);
        Check("visibility: show applies",
            show.Ok && !show.WasVisible && show.VisibleAfter && setCalls == 1,
            $"ok={show.Ok} was={show.WasVisible} after={show.VisibleAfter}");

        // 归属拒绝：owns=false → set 绝不被调用。
        var owned = false;
        var denied = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: false,
            ownsWindow: () => owned,
            isVisible: () => visible,
            setVisible: _ => { setCalls++; return true; },
            showOnlyIfWasVisible: true);
        Check("visibility: foreign/destroyed hwnd refused without write",
            !denied.Ok && !denied.Reason.Contains("returned false")
            && denied.Reason.Contains("ownership"),
            $"ok={denied.Ok} reason={denied.Reason}");

        // 原生 false：set 返回 false → Ok=false，但 VisibleAfter 如实读回。
        var nativeFalse = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: false,
            ownsWindow: () => true,
            isVisible: () => visible,
            setVisible: _ => false,
            showOnlyIfWasVisible: true);
        Check("visibility: native false reported honestly",
            !nativeFalse.Ok && nativeFalse.Reason.Contains("returned false"),
            $"ok={nativeFalse.Ok} reason={nativeFalse.Reason}");

        // 原生异常：set 抛 → Ok=false，异常不逃逸。
        var threw = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: false,
            ownsWindow: () => true,
            isVisible: () => visible,
            setVisible: _ => throw new InvalidOperationException("injected"),
            showOnlyIfWasVisible: true);
        Check("visibility: native throw contained and reported",
            !threw.Ok && threw.Reason.Contains("threw"),
            $"ok={threw.Ok} reason={threw.Reason}");

        // show + 原本不可见（showOnlyIfWasVisible=false）：只读回、不写。
        var beforeCalls = setCalls;
        var noShow = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: true,
            ownsWindow: () => true,
            isVisible: () => false,
            setVisible: _ => { setCalls++; return true; },
            showOnlyIfWasVisible: false);
        Check("visibility: originally-hidden child is not spuriously shown",
            noShow.Ok && !noShow.VisibleAfter && setCalls == beforeCalls,
            $"ok={noShow.Ok} after={noShow.VisibleAfter} calls={setCalls}");

        // 语义不匹配：hide 之后读回仍可见 → Ok=false（绝不把调用成功当已隐藏）。
        var mismatch = ChildSurfaceVisibility.RunSurfaceVisibilityOperation(
            show: false,
            ownsWindow: () => true,
            isVisible: () => true,          // 读回仍可见（原生未生效）
            setVisible: _ => true,
            showOnlyIfWasVisible: true);
        Check("visibility: applied hide but still visible is a failure",
            !mismatch.Ok && mismatch.Reason.Contains("mismatch"),
            $"ok={mismatch.Ok} reason={mismatch.Reason}");
    }

    /// <summary>
    /// R31 共享 TSF 文档焦点操作核的纯托管自检：acquire 自有验证/suspend 写+null 读回/
    /// restore 保存令牌验证/foreign 拒绝——全部经 <see cref="TsfDocumentFocus"/> 的
    /// 注入委托（测试替身对象模拟 COM 身份），无需窗口/线程/COM。验证控制流与
    /// "变更可能存在"旗标语义，不触碰任何真实 COM。
    /// </summary>
    /// <summary>COM-visible 自检令牌：Marshal.GetIUnknownForObject 需要可封送
    /// 类型；共享操作核用真实 IUnknown 指针比较身份，不是引用相等。</summary>
    [System.Runtime.InteropServices.ComVisible(true)]
    private sealed class SelfTestComToken
    {
    }

    private static void RunTsfFocusOpChecks()
    {
        // 模拟自有 doc/context（COM-visible 实例——共享核经
        // FocusSnapshot.CompareComIdentity 的 IUnknown 指针判同）。
        object ownDoc = new SelfTestComToken();
        object ownContext = new SelfTestComToken();
        object foreignDoc = new SelfTestComToken();
        object? currentFocus = ownDoc;
        var setFocusCalls = new List<object?>();
        var releaseCount = 0;
        var releaseErrors = new List<string>();
        var firstReleaseThrows = false;

        (int, object?) GetFocus() => (TsfHResult.S_OK, currentFocus);
        int SetFocus(object? doc) { setFocusCalls.Add(doc); currentFocus = doc; return TsfHResult.S_OK; }
        (int, object?) GetTop(object doc)
            => ReferenceEquals(doc, ownDoc) ? (TsfHResult.S_OK, ownContext) : (TsfHResult.S_OK, (object?)null);
        void Release(object _)
        {
            releaseCount++;
            if (firstReleaseThrows && releaseCount == 1)
            {
                throw new InvalidOperationException("injected release failure");
            }
        }

        // R31 复审：受控阶段跟踪（与宿主/测试替身共用同一类型）。
        var stage = new TsfFocusStage();
        string? NoGuard(string _) => null;

        // acquire：自有 doc+context → 有效令牌。
        var acquire = TsfDocumentFocus.Acquire(GetFocus, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: acquire own doc returns verified handle",
            acquire.Ok && acquire.Focus is { } h && ReferenceEquals(h.Doc, ownDoc)
            && ReferenceEquals(h.Context, ownContext),
            $"ok={acquire.Ok} reason={acquire.Reason}");

        // suspend：前置自有验证通过 → SetFocus(null) → 读回 null。
        setFocusCalls.Clear();
        var saved = acquire.Focus!;
        var suspend = TsfDocumentFocus.Suspend(GetFocus, SetFocus, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: suspend writes null and reads back null",
            suspend.Ok && suspend.MutationMayExist && currentFocus is null
            && setFocusCalls.Count == 1 && setFocusCalls[0] is null,
            $"ok={suspend.Ok} reason={suspend.Reason}");

        // restore：保存令牌重新 SetFocus → doc+context 读回同一。
        var restore = TsfDocumentFocus.Restore(saved, GetFocus, SetFocus, GetTop, Release);
        Check("tsf-focus: restore writes saved doc and verifies identity",
            restore.Ok && restore.MutationMayExist && ReferenceEquals(currentFocus, ownDoc)
            && setFocusCalls.Count == 2 && ReferenceEquals(setFocusCalls[1], ownDoc),
            $"ok={restore.Ok} reason={restore.Reason}");

        // acquire 拒绝 foreign doc（绝不夺取）。
        currentFocus = foreignDoc;
        var foreignAcquire = TsfDocumentFocus.Acquire(GetFocus, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: foreign document refused without write",
            !foreignAcquire.Ok && foreignAcquire.Reason.Contains("foreign"),
            $"ok={foreignAcquire.Ok} reason={foreignAcquire.Reason}");
        currentFocus = ownDoc;

        // suspend 前置 foreign：拒绝写（setFocusCalls 不变）。
        currentFocus = foreignDoc;
        var beforeCalls = setFocusCalls.Count;
        var foreignSuspend = TsfDocumentFocus.Suspend(GetFocus, SetFocus, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: suspend refuses foreign focus without write",
            !foreignSuspend.Ok && !foreignSuspend.MutationMayExist
            && setFocusCalls.Count == beforeCalls,
            $"ok={foreignSuspend.Ok} mutation={foreignSuspend.MutationMayExist}");
        currentFocus = ownDoc;

        // restore 前置 foreign：拒绝覆盖别人的焦点。
        currentFocus = foreignDoc;
        var foreignRestore = TsfDocumentFocus.Restore(saved, GetFocus, SetFocus, GetTop, Release);
        Check("tsf-focus: restore refuses to overwrite foreign focus",
            !foreignRestore.Ok && !foreignRestore.MutationMayExist
            && foreignRestore.Reason.Contains("foreign")
            && ReferenceEquals(currentFocus, foreignDoc),
            $"ok={foreignRestore.Ok} reason={foreignRestore.Reason}");
        currentFocus = ownDoc;

        // suspend 读回不生效（原生返回 S_OK 但焦点仍持有）→ 如实失败、mutation=true。
        int SetFocusLying(object? doc) { setFocusCalls.Add(doc); return TsfHResult.S_OK; } // 不真改 currentFocus
        var lyingSuspend = TsfDocumentFocus.Suspend(GetFocus, SetFocusLying, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: suspend readback mismatch reported honestly",
            !lyingSuspend.Ok && lyingSuspend.MutationMayExist,
            $"ok={lyingSuspend.Ok} reason={lyingSuspend.Reason}");

        // SetFocus 抛异常：异常包含在结果内、mutationMayExist=true（写可能已发生）。
        int SetFocusThrowing(object? _) => throw new InvalidOperationException("injected");
        var throwSuspend = TsfDocumentFocus.Suspend(GetFocus, SetFocusThrowing, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: set-focus throw contained and reported",
            !throwSuspend.Ok && throwSuspend.MutationMayExist
            && throwSuspend.Reason.Contains("threw"),
            $"ok={throwSuspend.Ok} reason={throwSuspend.Reason}");

        // GetFocus 返回失败 HRESULT → acquire 如实失败。
        (int, object?) BadGetFocus() => (TsfHResult.E_FAIL, null);
        var badAcquire = TsfDocumentFocus.Acquire(BadGetFocus, ownDoc, ownContext, GetTop, Release);
        Check("tsf-focus: get-focus failure reported honestly",
            !badAcquire.Ok && badAcquire.Reason.Contains("get-focus"),
            $"ok={badAcquire.Ok} reason={badAcquire.Reason}");

        // restore 读回不同 context（写已生效但 top 变了）→ 如实失败、mutation=true。
        (int, object?) WrongTop(object doc) => (TsfHResult.S_OK, new SelfTestComToken());
        var wrongTop = TsfDocumentFocus.Restore(saved, GetFocus, SetFocus, WrongTop, Release);
        Check("tsf-focus: restore readback context mismatch reported honestly",
            !wrongTop.Ok && wrongTop.MutationMayExist,
            $"ok={wrongTop.Ok} reason={wrongTop.Reason}");

        // ---- R31 复审：受控阶段 + 共享门禁 + 释放顺序 -------------------------------

        // 阶段初始为 OWN（acquire 后、suspend 前）。
        Check("tsf-focus: initial phase is own-document",
            stage.Phase == TsfFocusPhase.OwnDocument,
            $"phase={stage.Phase}");

        // suspend 已验证成功后阶段迁到 NULL；600ms 内自动回 OWN 经 ReadPhase 检出。
        currentFocus = ownDoc;
        var autoRefocus = TsfDocumentFocus.ReadPhase(GetFocus, ownDoc, ownContext,
            GetTop, Release, stage.Saved, stage.Phase, NoGuard, NoGuard);
        Check("tsf-focus: phase readback validates own focus",
            autoRefocus.Ok && autoRefocus.Actual == TsfFocusPhase.OwnDocument,
            $"ok={autoRefocus.Ok} actual={autoRefocus.Actual}");

        stage.MarkSuspended(saved);
        currentFocus = null;
        var suspendedPhase = TsfDocumentFocus.ReadPhase(GetFocus, ownDoc, ownContext,
            GetTop, Release, saved, stage.Phase, NoGuard, NoGuard);
        Check("tsf-focus: suspended phase reads back null",
            suspendedPhase.Ok && suspendedPhase.Actual == TsfFocusPhase.None,
            $"ok={suspendedPhase.Ok} actual={suspendedPhase.Actual}");

        // 等待期间焦点自动回 OWN（期望 NULL、实际 OWN）→ 如实拒绝。
        currentFocus = ownDoc;
        var autoReturn = TsfDocumentFocus.ReadPhase(GetFocus, ownDoc, ownContext,
            GetTop, Release, saved, stage.Phase, NoGuard, NoGuard);
        Check("tsf-focus: auto-refocus during suspended phase rejected",
            !autoReturn.Ok && autoReturn.Actual == TsfFocusPhase.OwnDocument,
            $"ok={autoReturn.Ok} actual={autoReturn.Actual} reason={autoReturn.Reason}");

        // 门禁拒绝（guard 返回非 null）→ 操作/读回如实失败且绝不写。
        string? DenyGuard(string _) => "foreign-foreground";
        currentFocus = ownDoc;
        var deniedSuspend = TsfDocumentFocus.Suspend(GetFocus, SetFocus, ownDoc,
            ownContext, GetTop, Release, DenyGuard, DenyGuard);
        Check("tsf-focus: guard refusal before set-focus never writes",
            !deniedSuspend.Ok && !deniedSuspend.MutationMayExist
            && deniedSuspend.Reason.Contains("guard"),
            $"ok={deniedSuspend.Ok} reason={deniedSuspend.Reason}");

        // 第一个 release 抛异常：第二个仍被释放——结果如实失败、含 release 错误。
        firstReleaseThrows = true;
        releaseErrors.Clear();
        releaseCount = 0;
        var releaseResult = TsfDocumentFocus.ReleaseHandle(saved, Release);
        Check("tsf-focus: first release throw still releases second",
            !releaseResult.Ok && releaseResult.Reason.Contains("release")
            && releaseCount == 2,
            $"ok={releaseResult.Ok} count={releaseCount} reason={releaseResult.Reason}");
        firstReleaseThrows = false;
    }

    private static int InsertText(AcpTextStore store, string text)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        try
        {
            return store.InsertTextAtSelection(0, pointer, (uint)text.Length,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static TsfTrace NoTrace() => (_, _, _) => { };

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"FAIL {name}{(detail.Length == 0 ? "" : $" — {detail}")}");
        }
    }
}


