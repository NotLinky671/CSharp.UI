using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R23 回归：诊断状态按 store 计数、按线程隔离且不继承；通知/锁请求为嵌套栈；
/// <see cref="FocusSnapshot"/> 的日志异常不穿透、身份查询失败与“不同”区分、释放异常可见。
/// 全部纯托管：不初始化 COM、不创建窗口、不发键，不声称证明任何 TIP 身份。
/// </summary>
public sealed class LayoutChainIsolationTests
{
    [Fact]
    public void NestedNotificationExitRestoresOuterId()
    {
        var chain = new LayoutChainTrace(true);

        var outer = chain.BeginNotification();
        chain.EnterNotification(outer);
        var inner = chain.BeginNotification();
        chain.EnterNotification(inner);
        Assert.Equal(inner, chain.CurrentNotificationId);
        Assert.Equal(2, chain.NotificationDepth);

        // 内层退出：必须恢复外层，而不是清零。
        chain.ExitNotification();
        Assert.Equal(outer, chain.CurrentNotificationId);
        Assert.Equal(1, chain.NotificationDepth);

        chain.ExitNotification();
        Assert.Equal(0, chain.CurrentNotificationId);
        Assert.Equal(0, chain.NotificationDepth);
    }

    [Fact]
    public void ReentrantLockRequestExitRestoresOuterId()
    {
        var chain = new LayoutChainTrace(true);

        var outer = chain.BeginLockRequest();
        chain.EnterLockRequest(outer);
        var inner = chain.BeginLockRequest();
        chain.EnterLockRequest(inner);
        Assert.Equal(inner, chain.CurrentLockRequestId);

        chain.ExitLockRequest();
        Assert.Equal(outer, chain.CurrentLockRequestId);
        Assert.Equal(1, chain.LockRequestDepth);

        chain.ExitLockRequest();
        Assert.Equal(0, chain.CurrentLockRequestId);
        Assert.Equal(0, chain.LockRequestDepth);
    }

    [Fact]
    public void NotificationAndLockNestIndependently()
    {
        var chain = new LayoutChainTrace(true);
        var notification = chain.BeginNotification();
        var lockRequest = chain.BeginLockRequest();

        chain.EnterNotification(notification);
        chain.EnterLockRequest(lockRequest);

        // 锁请求退出只影响锁栈，不应清掉通知 ID。
        chain.ExitLockRequest();
        Assert.Equal(notification, chain.CurrentNotificationId);
        Assert.Equal(0, chain.CurrentLockRequestId);
        Assert.Equal(LayoutChainScope.TsfCallback, chain.DescribeScope());

        chain.ExitNotification();
        Assert.Equal(0, chain.CurrentNotificationId);
    }

    [Fact]
    public void AppScopeDoesNotLeakToAnotherThreadOnSameStore()
    {
        var chain = new LayoutChainTrace(true);

        using (chain.PushScope(LayoutChainScope.RequestSession))
        {
            Assert.Equal(LayoutChainScope.RequestSession, chain.DescribeScope());

            // 另一线程不得读到本线程的 probe 标签（诊断描述原生 UI 线程调用嵌套）。
            var otherThread = RunOnNewThread(chain.DescribeScope);
            Assert.Equal(LayoutChainScope.Unknown, otherThread);

            // 通知 ID 同样按线程隔离。
            var notification = chain.BeginNotification();
            chain.EnterNotification(notification);
            var otherThreadNotification = RunOnNewThread(() => chain.CurrentNotificationId);
            Assert.Equal(0, otherThreadNotification);
            chain.ExitNotification();
        }

        Assert.Equal(LayoutChainScope.Unknown, chain.DescribeScope());
    }

    [Fact]
    public void UnsafeQueueUserWorkItemThreadSeesItsOwnEmptyScope()
    {
        // 诊断状态以真实 OS 线程 ID 为键：在另一线程上执行的工作项读到的是它自己的空栈。
        // 若工作项被内联到同一线程，读到的就是本线程标签——这不是泄漏，而是同一线程。
        // 因此这里先用线程身份判定，只在确实换了线程时断言隔离。
        // 用 ThreadPool.UnsafeQueueUserWorkItem + 信号等待，避免阻塞式 task 操作。
        var chain = new LayoutChainTrace(true);
        var mainThreadId = Environment.CurrentManagedThreadId;

        using (chain.PushScope(LayoutChainScope.StoreDirectQuery))
        {
            var done = new ManualResetEventSlim(false);
            var seen = "unset";
            var ranOnOtherThread = false;

            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                seen = chain.DescribeScope();
                ranOnOtherThread = Environment.CurrentManagedThreadId != mainThreadId;
                done.Set();
            }, null);

            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "work item did not finish");

            if (ranOnOtherThread)
            {
                Assert.Equal(LayoutChainScope.Unknown, seen);
            }
            else
            {
                // 内联执行：同一线程读到自己标签是正确行为，记录该事实而非伪装通过。
                Assert.Equal(LayoutChainScope.StoreDirectQuery, seen);
            }
        }
    }

    [Fact]
    public void ContinuationThreadDoesNotInheritScope()
    {
        // 在另一个线程池线程上读取：诊断状态以 OS 线程 ID 为键，该线程读到自己的空栈。
        var chain = new LayoutChainTrace(true);
        var mainThreadId = Environment.CurrentManagedThreadId;

        using (chain.PushScope(LayoutChainScope.ViewQuery))
        {
            var done = new ManualResetEventSlim(false);
            var seen = "unset";
            var otherThread = false;

            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                seen = chain.DescribeScope();
                otherThread = Environment.CurrentManagedThreadId != mainThreadId;
                done.Set();
            }, null);

            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(otherThread, "work item unexpectedly ran on the calling thread");
            Assert.Equal(LayoutChainScope.Unknown, seen);
        }
    }

    /// <summary>在显式新建的真实线程上执行并取回结果（不依赖线程池复用或同步上下文）。</summary>
    private static T RunOnNewThread<T>(Func<T> body)
    {
        T result = default!;
        var thread = new Thread(() => result = body()) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "helper thread did not finish");
        return result;
    }

    [Fact]
    public void NewThreadStartsWithoutInheritedState()
    {
        var chain = new LayoutChainTrace(true);
        var notification = chain.BeginNotification();
        chain.EnterNotification(notification);

        // 显式起一个真实新线程：不能继承创建线程的通知/阶段状态。
        var seen = "unset";
        var thread = new Thread(() => seen = chain.DescribeScope());
        thread.Start();
        thread.Join();

        Assert.Equal(LayoutChainScope.Unknown, seen);
        Assert.Equal(notification, chain.CurrentNotificationId);
        chain.ExitNotification();
    }

    [Fact]
    public void AppScopeDoesNotLeakToAnotherStore()
    {
        var first = new LayoutChainTrace(true);
        var second = new LayoutChainTrace(true);

        using (first.PushScope(LayoutChainScope.ViewQuery))
        {
            Assert.Equal(LayoutChainScope.ViewQuery, first.DescribeScope());
            Assert.Equal(LayoutChainScope.Unknown, second.DescribeScope());
        }
    }

    [Fact]
    public void IdCountersArePerStoreAndMonotonic()
    {
        var first = new LayoutChainTrace(true);
        var second = new LayoutChainTrace(true);

        var firstA = first.BeginNotification();
        var firstB = first.BeginNotification();
        var secondA = second.BeginNotification();

        Assert.Equal(firstA + 1, firstB);
        // 另一个 store 的计数独立，不共享序列。
        Assert.Equal(1, secondA);
        Assert.Equal(1, first.BeginLockRequest());
        Assert.Equal(1, second.BeginLockRequest());
    }

    [Fact]
    public void ScopeDisposeFollowsStrictLifoOrder()
    {
        var chain = new LayoutChainTrace(true);

        var outer = chain.PushScope(LayoutChainScope.RequestSession);
        var inner = chain.PushScope(LayoutChainScope.ViewQuery);
        Assert.Equal(LayoutChainScope.ViewQuery, chain.DescribeScope());
        Assert.Equal(2, chain.ScopeDepth);

        // 严格 LIFO：内层先释放，恢复外层。
        inner.Dispose();
        Assert.Equal(LayoutChainScope.RequestSession, chain.DescribeScope());
        Assert.Equal(1, chain.ScopeDepth);

        outer.Dispose();
        Assert.Equal(LayoutChainScope.Unknown, chain.DescribeScope());
        Assert.Equal(0, chain.ScopeDepth);
    }

    [Fact]
    public void SameNameNestedScopeDuplicateDisposeIsNotGuaranteed()
    {
        // 记录实际契约：本类型**不**提供幂等保证。同名嵌套时，第二次 Dispose 会继续弹出外层。
        // 该用例固定这一事实，避免再次把“不同 scope 名称”误当作一般幂等证明。
        var chain = new LayoutChainTrace(true);

        var outer = chain.PushScope(LayoutChainScope.RequestSession);
        var inner = chain.PushScope(LayoutChainScope.RequestSession);
        Assert.Equal(2, chain.ScopeDepth);

        inner.Dispose();
        Assert.Equal(LayoutChainScope.RequestSession, chain.DescribeScope());
        Assert.Equal(1, chain.ScopeDepth);

        // 同名重复释放：仍会弹掉外层——这是已知且已文档化的限制，不是被隐藏的行为。
        inner.Dispose();
        Assert.Equal(0, chain.ScopeDepth);
        Assert.Equal(LayoutChainScope.Unknown, chain.DescribeScope());

        // 之后 outer.Dispose 无栈可弹，但不得抛出。
        outer.Dispose();
        Assert.Equal(0, chain.ScopeDepth);
        Assert.Equal(LayoutChainScope.Unknown, chain.DescribeScope());
    }

    [Fact]
    public void ThrowingLoggerDoesNotEscapeFocusSnapshotCapture()
    {
        var chain = new LayoutChainTrace(true);
        TsfTrace throwing = (_, _, _) => throw new InvalidOperationException("logger boom");

        // 日志委托异常不得穿透：Capture 只做诊断，不能改变宿主流程。
        var exception = Record.Exception(() => FocusSnapshot.Capture(chain, throwing, "test",
            IntPtr.Zero, null, null, null));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowingLoggerDoesNotEscapeLayoutNotificationTrace()
    {
        // store 的 Trace 自身兜住日志异常，因此通知结果不受影响。
        var throwing = new TsfTrace((_, _, _) => throw new InvalidOperationException("logger boom"));
        var store = new AcpTextStore(throwing, new LayoutChainTrace(true));
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK,
            store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        var ok = store.NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);

        // sink 返回 S_OK → 通知成功；日志异常不改变该结果。
        Assert.True(ok);
    }

    [Fact]
    public void DisabledFocusSnapshotNeverCallsLogger()
    {
        var chain = new LayoutChainTrace(false);
        var called = false;
        TsfTrace counting = (_, _, _) => called = true;

        FocusSnapshot.Capture(chain, counting, "test", IntPtr.Zero, null, null, null);

        Assert.False(called);
    }

    [Fact]
    public void ComIdentityReportsErrorInsteadOfDifferentOnQueryFailure()
    {
        // 两个不同的托管对象各自被包装成不同 CCW：这是真正的 Different（不是 Error）。
        var different = FocusSnapshot.CompareComIdentity(new object(), new object());
        Assert.Equal(ComIdentity.Different, different.Identity);
        Assert.False(different.Same);
        Assert.False(different.Errored);

        // 无法取 IUnknown 的输入（null）必须报 Error，不能伪装成 Different。
        var errored = FocusSnapshot.CompareComIdentity(null!, new object());
        Assert.True(errored.Errored);
        Assert.False(errored.Same);
        Assert.NotNull(errored.Detail);
    }

    [Fact]
    public void SameComIdentityReturnsFalseButDetailPreservesFailure()
    {
        // 兼容旧布尔 API：失败仍返回 false，但三态结果保留原因。
        Assert.False(FocusSnapshot.SameComIdentity(null!, new object()));

        var result = FocusSnapshot.CompareComIdentity(null!, new object());
        Assert.True(result.Errored);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void FocusSnapshotDistinguishesNoFocusFromOtherDocumentManager()
    {
        // 没有 threadMgr：unavailable，而不是“其他 document manager”。
        Assert.Equal(FocusSnapshot.Unavailable,
            FocusSnapshot.DescribeTsfFocus(null, null, null));
    }

    [Fact]
    public void FocusSnapshotRecordsReleaseErrorsVisibly()
    {
        // 无法取 IUnknown 的输入：必须留下原因，而不是被吞掉后当成“不同对象”。
        var errors = new List<string>();
        var result = FocusSnapshot.CompareComIdentity(null!, new object(), errors);

        Assert.True(result.Errored);
        Assert.NotNull(result.Detail);
        // 失败原因进入结果（而非被吞掉），便于 Capture 写入 tsf_focus。
        Assert.DoesNotContain("Different", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureReportsReleaseOutcomeFields()
    {
        var chain = new LayoutChainTrace(true);
        var fields = new Dictionary<string, object?>();
        TsfTrace capture = (_, kind, f) =>
        {
            if (kind != "layout-chain" || f is null)
            {
                return;
            }

            foreach (var pair in f)
            {
                fields[pair.Key] = pair.Value;
            }
        };

        FocusSnapshot.Capture(chain, capture, "test", IntPtr.Zero, null, null, null);

        // 未启用任何 COM：应如实报 unavailable，并显式给出释放结论字段。
        Assert.Equal(FocusSnapshot.Unavailable, fields["tsf_focus"]);
        Assert.True(fields.ContainsKey("all_releases_succeeded"));
        Assert.True(fields.ContainsKey("release_errors"));
        Assert.Equal(false, fields["atomic"]);
    }
}
