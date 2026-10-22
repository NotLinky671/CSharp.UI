using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R29B：组合连续性单调计数（begin/end/session）必须随 <c>SnapshotForLifecycle</c>
/// 在同一把锁内原子返回；只有真实 Begin/End 才递增，失败 Begin、冗余 End、
/// 追踪取消一律不伪造计数。
/// </summary>
public sealed class CompositionContinuityTests
{
    [Fact]
    public void SnapshotIncludesContinuityCountersAtomically()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);

        var initial = store.SnapshotForLifecycle();
        Assert.Equal(0, initial.BeginCount);
        Assert.Equal(0, initial.EndCount);
        Assert.Equal(0, initial.SessionId);

        Assert.True(store.BeginComposition(0, 0));
        var afterBegin = store.SnapshotForLifecycle();
        Assert.True(afterBegin.Composing);
        Assert.Equal(1, afterBegin.BeginCount);
        Assert.Equal(0, afterBegin.EndCount);
        Assert.Equal(1, afterBegin.SessionId);
    }

    [Fact]
    public void RealEndIncrementsEndCount()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);
        Assert.True(store.BeginComposition(0, 0));

        store.EndComposition();

        var snapshot = store.SnapshotForLifecycle();
        Assert.False(snapshot.Composing);
        Assert.Equal(1, snapshot.BeginCount);
        Assert.Equal(1, snapshot.EndCount);
        Assert.Equal(1, snapshot.SessionId);
    }

    [Fact]
    public void FailedBeginDoesNotIncrementAnyCounter()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.ResetDocument("ab");

        // 越界范围 → Begin 失败，任何计数都不能动。
        Assert.False(store.BeginComposition(5, 4));

        var snapshot = store.SnapshotForLifecycle();
        Assert.False(snapshot.Composing);
        Assert.Equal(0, snapshot.BeginCount);
        Assert.Equal(0, snapshot.EndCount);
        Assert.Equal(0, snapshot.SessionId);
    }

    [Fact]
    public void RedundantEndDoesNotIncrementEndCount()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);
        Assert.True(store.BeginComposition(0, 0));
        store.EndComposition();

        // 已经不在组合中：幂等 End 不得再加 EndCount。
        store.EndComposition();
        store.EndComposition();

        var snapshot = store.SnapshotForLifecycle();
        Assert.Equal(1, snapshot.BeginCount);
        Assert.Equal(1, snapshot.EndCount);
    }

    [Fact]
    public void SameTextRestartIsDetectedByCounters()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);

        // 第一次组合：写入 "ni" 后用真实 UpdateComposition 固定 span，
        // 并在**组合仍活跃**时取第一组快照（text/span/计数原子一致）。
        Assert.True(store.BeginComposition(0, 0));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.InsertText(store, "ni"));
        Assert.True(store.UpdateComposition(0, 2));
        var first = store.SnapshotForLifecycle();
        Assert.True(first.Composing);
        Assert.Equal("ni", first.Text);
        Assert.Equal((0, 2), (first.Start, first.End));
        store.EndComposition();

        // 用**相同文本与相同范围**重启组合：text/span/composing 完全相等，
        // 只有 begin/end/session 单调计数证明这是新的组合（真实不连续）。
        Assert.True(store.BeginComposition(0, 2));
        var second = store.SnapshotForLifecycle();

        Assert.Equal(first.Text, second.Text);
        Assert.Equal((first.Start, first.End), (second.Start, second.End));
        Assert.True(second.Composing);
        Assert.Equal(2, second.BeginCount);
        Assert.Equal(1, second.EndCount);
        Assert.Equal(2, second.SessionId);
        Assert.NotEqual(first.BeginCount, second.BeginCount);
        Assert.NotEqual(first.SessionId, second.SessionId);

        // 用**真实快照**喂给生产不变量：两次快照 text/span/composing 完全一致，
        // 但 SameInvariant 必须因连续性计数不同而拒绝（不是手编计数对拍）。
        var firstLive = new LifecycleSnapshot(first.Text, first.Start, first.End,
            Writes: 0, Composing: first.Composing, Profile: "baseline-ime",
            first.BeginCount, first.EndCount, first.SessionId);
        var secondLive = new LifecycleSnapshot(second.Text, second.Start, second.End,
            Writes: 0, Composing: second.Composing, Profile: "baseline-ime",
            second.BeginCount, second.EndCount, second.SessionId);

        Assert.True(secondLive.SameComposition(firstLive));
        Assert.False(secondLive.SameInvariant(firstLive));
        Assert.Contains("continuity", secondLive.DescribeInvariantViolation(firstLive));
    }

    [Fact]
    public void EachSuccessfulBeginAllocatesNewSessionId()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);

        Assert.True(store.BeginComposition(0, 0));
        var s1 = store.SnapshotForLifecycle().SessionId;
        store.EndComposition();
        Assert.True(store.BeginComposition(0, 0));
        var s2 = store.SnapshotForLifecycle().SessionId;

        Assert.True(s2 > s1, $"session id must be monotonic (s1={s1}, s2={s2})");
    }

    [Fact]
    public void CancelCompositionTrackingDoesNotIncrementEndCount()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);
        Assert.True(store.BeginComposition(0, 0));

        // 追踪取消（宿主清理路径）不是一次真实 EndComposition，不伪造 end 计数。
        store.CancelCompositionTracking();

        var snapshot = store.SnapshotForLifecycle();
        Assert.False(snapshot.Composing);
        Assert.Equal(1, snapshot.BeginCount);
        Assert.Equal(0, snapshot.EndCount);
    }
}
