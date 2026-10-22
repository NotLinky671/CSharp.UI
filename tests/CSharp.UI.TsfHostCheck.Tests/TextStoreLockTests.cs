using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class TextStoreLockTests
{
    [Fact]
    public void NoSinkFailsLockRequest()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(
            TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out var sessionHr));
        Assert.Equal(TsfHResult.E_FAIL, sessionHr);
    }

    [Fact]
    public void SynchronousRequestGrantsImmediately()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var sawWriteLock = false;

        var hr = store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out var sessionHr);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(TsfHResult.S_OK, sessionHr);
        Assert.Equal(1, sink.LockGrantedCount);

        sink.LockGranted = _ =>
        {
            sawWriteLock = store.Locks.IsWriteLocked;
            return TsfHResult.S_OK;
        };
        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        Assert.True(sawWriteLock);
        Assert.False(store.Locks.IsWriteLocked);
    }

    [Fact]
    public void NestedSynchronousRequestIsRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var nested = 0;
        sink.LockGranted = _ =>
        {
            store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out nested);
            return TsfHResult.S_OK;
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);
        Assert.Equal(TsfHResult.TS_E_SYNCHRONOUS, nested);
    }

    [Fact]
    public void AsynchronousRequestQueuesAndDrainsAfterRelease()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var queued = 0;
        var grants = 0;
        sink.LockGranted = flags =>
        {
            grants++;
            if (grants == 1)
            {
                store.RequestLock(TsfFlags.TS_LF_READ, out queued);
            }

            return TsfHResult.S_OK;
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out _);

        Assert.Equal(TsfHResult.TS_S_ASYNC, queued);
        Assert.Equal(2, grants);
        Assert.Equal(0, store.Locks.PendingCount);
        Assert.Equal(TsfFlags.TS_LF_READ, sink.LastLockFlags);
    }

    [Fact]
    public void InvalidLockFlagsAreRejected()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_SYNC, out var sessionHr));
        Assert.Equal(TsfHResult.E_INVALIDARG, sessionHr);
    }

    [Fact]
    public void MutationsWithoutWriteLockReturnNoLock()
    {
        var (store, sink) = StoreTestHelpers.NewStore();

        Assert.Equal(TsfHResult.TS_E_NOLOCK, StoreTestHelpers.SetText(store, 0, 0, "a"));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, StoreTestHelpers.InsertText(store, "a"));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.ApplyAppText("a"));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.ApplyAppBackspace());
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.ApplyAppSelection(0, 0));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.SetSelection(0, IntPtr.Zero));
        Assert.Equal("", store.Document.Text);
    }

    [Fact]
    public void ReadLockCannotWrite()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var writeHr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ,
            () => writeHr = StoreTestHelpers.SetText(store, 0, 0, "a"));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, writeHr);
    }

    [Fact]
    public void QueriesWithoutReadLockReturnNoLock()
    {
        var (store, _) = StoreTestHelpers.NewStore();

        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetEndACP(out _));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetTextExt(1, 0, 0, out _, out _));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetACPFromPoint(1, IntPtr.Zero, 0, out _));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetText(0, 0, IntPtr.Zero, 0, out _, IntPtr.Zero, 0, out _, out _));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetSelection(0, 1, IntPtr.Zero, out _));
    }

    /// <summary>
    /// R24：`GetScreenExt` 是显示表面查询，官方样例注明 “no lock is necessary for this method.”，
    /// 因此不得返回 `TS_E_NOLOCK`；只有非法 view 才是 `E_INVALIDARG`。
    /// </summary>
    [Fact]
    public void ScreenExtDoesNotRequireReadLock()
    {
        var (store, _) = StoreTestHelpers.NewStore();

        // 无锁、有真实表面数据：必须成功。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(10, 20, 410, 220), true, null));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var rect));
        Assert.Equal(TsfRect.FromLtrb(10, 20, 410, 220), rect);

        // 无锁、非法 view：E_INVALIDARG，输出零矩形。
        Assert.Equal(TsfHResult.E_INVALIDARG, store.GetScreenExt(99, out var invalid));
        Assert.Equal(default(TsfRect), invalid);
    }

    [Fact]
    public void LockSessionsIncrement()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var sessions = new List<long>();
        sink.LockGranted = _ =>
        {
            sessions.Add(store.Locks.LastSession);
            return TsfHResult.S_OK;
        };
        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);
        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);
        Assert.Equal(new long[] { 1, 2 }, sessions);
        Assert.Equal(2, store.Locks.LastSession);
    }

    [Fact]
    public void UnknownLockFlagsAreRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var grants = 0;
        sink.LockGranted = _ =>
        {
            grants++;
            return TsfHResult.S_OK;
        };

        Assert.Equal(TsfHResult.S_OK,
            store.RequestLock(TsfFlags.TS_LF_READ | 0x100, out var unknownSession));
        Assert.Equal(TsfHResult.E_INVALIDARG, unknownSession);
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(0x4, out var writeOnlySession));
        Assert.Equal(TsfHResult.E_INVALIDARG, writeOnlySession);
        Assert.Equal(0, grants);
    }

    [Fact]
    public void ReadLockAsyncWriteUpgradeCoalescesAndGrants()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var readQueued = 0;
        var writeQueued = 0;
        var grants = 0;
        sink.LockGranted = _ =>
        {
            grants++;
            if (grants == 1)
            {
                store.RequestLock(TsfFlags.TS_LF_READ, out readQueued);
                store.RequestLock(TsfFlags.TS_LF_READWRITE, out writeQueued);
            }

            return TsfHResult.S_OK;
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);

        Assert.Equal(TsfHResult.TS_S_ASYNC, readQueued);
        Assert.Equal(TsfHResult.TS_S_ASYNC, writeQueued);
        Assert.Equal(2, grants);
        Assert.Equal(TsfFlags.TS_LF_READWRITE, sink.LastLockFlags);
        Assert.Equal(0, store.Locks.PendingCount);
    }

    [Fact]
    public void ManyQueuedReadsStayBounded()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var grants = 0;
        var results = new List<int>();
        sink.LockGranted = _ =>
        {
            grants++;
            if (grants == 1)
            {
                for (var i = 0; i < 200; i++)
                {
                    store.RequestLock(TsfFlags.TS_LF_READ, out var queued);
                    results.Add(queued);
                }
            }

            return TsfHResult.S_OK;
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);

        Assert.All(results, hr => Assert.Equal(TsfHResult.TS_S_ASYNC, hr));
        Assert.Equal(2, grants);
        Assert.Equal(0, store.Locks.PendingCount);
    }

    [Fact]
    public void FailedSyncDoesNotDropPendingWriteUpgrade()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var asyncHr = 0;
        var syncHr = 0;
        var grants = 0;
        sink.LockGranted = _ =>
        {
            grants++;
            if (grants == 1)
            {
                store.RequestLock(TsfFlags.TS_LF_READWRITE, out asyncHr);
                store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READWRITE, out syncHr);
            }

            return TsfHResult.S_OK;
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);

        Assert.Equal(TsfHResult.TS_S_ASYNC, asyncHr);
        Assert.Equal(TsfHResult.TS_E_SYNCHRONOUS, syncHr);
        Assert.Equal(2, grants);
        Assert.Equal(TsfFlags.TS_LF_READWRITE, sink.LastLockFlags);
        Assert.Equal(0, store.Locks.PendingCount);
    }

    [Fact]
    public void DrainLimitContinuesPendingUpgradeInSafePhase()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var posted = new Queue<Action>();
        store.NotificationDispatcher = posted.Enqueue;
        var grants = 0;
        sink.LockGranted = flags =>
        {
            grants++;
            if (grants <= 20)
            {
                store.RequestLock(TsfFlags.TS_LF_READWRITE, out _);
            }

            return TsfHResult.S_OK;
        };

        store.RequestLock(TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out _);

        // 1 次初始 + 16 次排空；第 17 次授予又入队了写升级，达到上限后保留 pending。
        Assert.Equal(17, grants);
        Assert.Equal(1, store.Locks.PendingCount);
        Assert.NotEmpty(posted);

        var pumped = 0;
        while (posted.Count > 0 && pumped++ < 10)
        {
            posted.Dequeue()();
        }

        Assert.Equal(21, grants);
        Assert.Equal(0, store.Locks.PendingCount);
        Assert.Empty(posted);
    }

    /// <summary>
    /// R20：OnLockGranted 只接收授予的锁类型（READ/READWRITE），不能回传请求用的 TS_LF_SYNC。
    /// 回传 0x3 这类组合会让 TSF 的编辑会话调度进入非预期路径。
    /// </summary>
    [Theory]
    [InlineData(TsfFlags.TS_LF_READ, TsfFlags.TS_LF_READ)]
    [InlineData(TsfFlags.TS_LF_READWRITE, TsfFlags.TS_LF_READWRITE)]
    public void GrantedLockFlagsNeverIncludeTheRequestSyncBit(uint requested, uint expected)
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var granted = uint.MaxValue;
        sink.LockGranted = grantedFlags =>
        {
            granted = grantedFlags;
            return TsfHResult.S_OK;
        };

        Assert.Equal(TsfHResult.S_OK,
            store.RequestLock(TsfFlags.TS_LF_SYNC | requested, out var sessionHr));

        Assert.Equal(TsfHResult.S_OK, sessionHr);
        Assert.Equal(expected, granted);
        Assert.Equal(0u, granted & TsfFlags.TS_LF_SYNC);
    }
}
