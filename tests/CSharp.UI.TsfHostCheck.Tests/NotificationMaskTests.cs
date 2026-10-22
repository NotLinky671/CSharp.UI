using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class NotificationMaskTests
{
    [Fact]
    public void WrongSinkIidIsRejected()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.TS_E_NOINTERFACE,
            store.AdviseSink(Guid.NewGuid(), sink, TsfFlags.TS_AS_ALL_SINKS));
        Assert.False(store.HasSink);
    }

    [Fact]
    public void SecondAdviseIsRejected()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        Assert.Equal(TsfHResult.E_INVALIDARG,
            store.AdviseSink(TsfIids.TextStoreAcpSink, new RecordingTextStoreSink(), TsfFlags.TS_AS_ALL_SINKS));
    }

    [Fact]
    public void UnadviseWithDifferentSinkIsRejectedAndKeepsCurrentSink()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        Assert.Equal(TsfHResult.E_INVALIDARG, store.UnadviseSink(new RecordingTextStoreSink()));
        Assert.True(store.HasSink);

        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));
        Assert.False(store.HasSink);
    }

    [Fact]
    public void TextMaskGatesSelectionAndLayout()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_TEXT_CHANGE);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("abc"));

        Assert.Equal(1, sink.TextChangeCount);
        Assert.Equal(0, sink.SelectionChangeCount);
        Assert.Equal(0, sink.LayoutChangeCount);

        StoreTestHelpers.Publish(store, new[] { 10, 10, 10 });
        Assert.Equal(0, sink.LayoutChangeCount);
    }

    [Fact]
    public void TsfOriginWritesDoNotEchoNotifications()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        sink.LockTypeProbe = () => store.Locks.LockType;

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "abc"));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.InsertText(store, "d"));

        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(
            System.Runtime.InteropServices.Marshal.SizeOf<TsSelectionAcp>());
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(
                new TsSelectionAcp { AcpStart = 1, AcpEnd = 1 }, pointer, false);
            StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
                () => store.SetSelection(1, pointer));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);
        }

        Assert.Equal(0, sink.TextChangeCount);
        Assert.Equal(0, sink.SelectionChangeCount);
        Assert.Empty(sink.LockTypesDuringNotifications);
    }

    [Fact]
    public void AppOriginNotificationsOccurAfterLockRelease()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        sink.LockTypeProbe = () => store.Locks.LockType;

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("a"));

        Assert.Equal(1, sink.TextChangeCount);
        Assert.NotEmpty(sink.LockTypesDuringNotifications);
        Assert.All(sink.LockTypesDuringNotifications,
            lockType => Assert.Equal(0u, lockType));
    }

    [Fact]
    public void NotificationsDeferredUntilAfterRequestLockReturns()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        var pending = new Queue<Action>();
        store.NotificationDispatcher = pending.Enqueue;
        store.ResetDocument(string.Empty);
        var nestedHr = int.MinValue;
        sink.OnTextChangedHook = () =>
            store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out nestedHr);
        sink.LockGranted = _ => store.ApplyAppText("a");

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(
            TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _));

        // RequestLock 已返回：锁深度 0，通知尚未在调用栈内送达。
        Assert.Equal(0u, store.Locks.LockType);
        Assert.Equal(0, sink.TextChangeCount);
        Assert.Single(pending);

        sink.LockGranted = _ => TsfHResult.S_OK;
        pending.Dequeue()();

        Assert.Equal(1, sink.TextChangeCount);
        Assert.Equal(TsfHResult.S_OK, nestedHr);
        Assert.Equal(0u, store.Locks.LockType);

        // 回调完成后仍可再次取得锁。
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(
            TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out var sessionHr));
        Assert.Equal(TsfHResult.S_OK, sessionHr);
    }

    [Fact]
    public void MultipleNotificationsShareOneScheduledFlush()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        var pending = new Queue<Action>();
        store.NotificationDispatcher = pending.Enqueue;
        store.ResetDocument(string.Empty);
        sink.LockGranted = _ =>
        {
            var hr = store.ApplyAppText("a");
            return hr < 0 ? hr : store.ApplyAppText("b");
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);

        Assert.Equal(0, sink.TextChangeCount);
        Assert.Single(pending);
        pending.Dequeue()();
        Assert.Equal(2, sink.TextChangeCount);
    }

    [Fact]
    public void NoDispatcherKeepsNotificationsPendingUntilManualFlush()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        store.ResetDocument(string.Empty);
        sink.LockGranted = _ => store.ApplyAppText("a");

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(
            TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _));

        // 没有 dispatcher 时不创建任何跨线程 Timer：通知保留 pending。
        Assert.Equal(0, sink.TextChangeCount);
        Assert.Equal(0u, store.Locks.LockType);

        // 调用方在原线程安全阶段显式 flush。
        store.FlushDeferredNotifications();
        Assert.Equal(1, sink.TextChangeCount);
    }

    [Fact]
    public void UnadviseDropsQueuedNotifications()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        var posted = new Queue<Action>();
        store.NotificationDispatcher = posted.Enqueue;
        store.ResetDocument(string.Empty);
        sink.LockGranted = _ => store.ApplyAppText("a");

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        Assert.Single(posted);

        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));
        while (posted.Count > 0)
        {
            posted.Dequeue()();
        }

        Assert.Equal(0, sink.TextChangeCount);
    }

    [Fact]
    public void CleanupDropsQueuedNotifications()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        var posted = new Queue<Action>();
        store.NotificationDispatcher = posted.Enqueue;
        store.ResetDocument(string.Empty);
        sink.LockGranted = _ => store.ApplyAppText("a");

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        var host = new TsfSessionHost(store, IntPtr.Zero, null, StoreTestHelpers.NoTrace,
            sta: false, () => { });
        host.Cleanup();

        while (posted.Count > 0)
        {
            posted.Dequeue()();
        }

        Assert.Equal(0, sink.TextChangeCount);
    }

    [Fact]
    public void EngageWithoutDispatcherFailsBeforeCom()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var host = new TsfSessionHost(store, IntPtr.Zero, null, StoreTestHelpers.NoTrace,
            sta: false, () => { });

        Assert.Equal(TsfHResult.E_FAIL, host.Engage());
        Assert.Equal(0, host.CleanupFailures);
        Assert.False(host.ResourceState.ComInitialized);
    }

    [Fact]
    public void WriteBitWithoutReadBitIsRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var granted = false;
        sink.LockGranted = _ =>
        {
            granted = true;
            return TsfHResult.S_OK;
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(0x4, out var sessionHr));
        Assert.Equal(TsfHResult.E_INVALIDARG, sessionHr);
        Assert.False(granted);
    }

    [Fact]
    public void SelectionMaskGatesSelection()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_SEL_CHANGE);
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppSelection(0, 0));
        Assert.Equal(1, sink.SelectionChangeCount);
        Assert.Equal(0, sink.TextChangeCount);

        var (textStore, textSink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_TEXT_CHANGE);
        StoreTestHelpers.UnderLock(textStore, textSink, TsfFlags.TS_LF_READWRITE,
            () => textStore.ApplyAppSelection(0, 0));
        Assert.Equal(0, textSink.SelectionChangeCount);
    }

    [Fact]
    public void LayoutMaskGatesLayout()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_LAYOUT_CHANGE);
        StoreTestHelpers.Publish(store, Array.Empty<int>(), notify: true);
        Assert.Equal(2, sink.LayoutChangeCount);
        Assert.Equal(TsfFlags.TS_LC_CREATE, sink.LayoutChanges[0].Code);
        Assert.Equal(TsfFlags.TS_LC_CHANGE, sink.LayoutChanges[1].Code);
        Assert.Equal(1u, sink.LayoutChanges[0].View);

        StoreTestHelpers.Publish(store, Array.Empty<int>(), notify: true);
        Assert.Equal(3, sink.LayoutChangeCount);
        Assert.Equal(TsfFlags.TS_LC_CHANGE, sink.LayoutChanges[2].Code);
    }

    [Fact]
    public void ReadvisedSinkGetsLayoutCreateAgain()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_LAYOUT_CHANGE);
        StoreTestHelpers.Publish(store, Array.Empty<int>(), notify: true);
        Assert.Equal(2, sink.LayoutChangeCount);

        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));
        var second = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK, store.AdviseSink(
            TsfIids.TextStoreAcpSink, second, TsfFlags.TS_AS_LAYOUT_CHANGE));
        StoreTestHelpers.Publish(store, Array.Empty<int>(), notify: true);

        Assert.Equal(2, second.LayoutChangeCount);
        Assert.Equal(TsfFlags.TS_LC_CREATE, second.LayoutChanges[0].Code);
        Assert.Equal(TsfFlags.TS_LC_CHANGE, second.LayoutChanges[1].Code);
    }

    [Fact]
    public void LayoutDestroyIsSentOnce()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_LAYOUT_CHANGE);
        StoreTestHelpers.Publish(store, Array.Empty<int>(), notify: true);
        store.NotifyLayoutDestroyed();
        store.NotifyLayoutDestroyed();
        Assert.Equal(3, sink.LayoutChangeCount);
        Assert.Equal(TsfFlags.TS_LC_DESTROY, sink.LayoutChanges[2].Code);
    }

    [Fact]
    public void UnadvisedSinkReceivesNothing()
    {
        var (store, sink) = StoreTestHelpers.NewStore(TsfFlags.TS_AS_ALL_SINKS);
        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("a"));
        Assert.Equal(0, sink.TextChangeCount);
    }

    [Fact]
    public void NoSinkDoesNotCrashNotifications()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        store.ResetDocument("abc");
        store.InvalidateLayout("no-sink");
        Assert.False(store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE));
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(
            TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out var sessionHr));
        Assert.Equal(TsfHResult.E_FAIL, sessionHr);
    }
}
