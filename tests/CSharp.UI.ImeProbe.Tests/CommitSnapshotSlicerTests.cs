using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 逐结果快照切片的确定性测试。
/// </summary>
/// <remarks>
/// 回归的缺陷：分两段提交时，第二段的组合会话会清空“当前预编辑历史”，如果拿最后一段的历史去判定第一段结果，
/// 第一段必然失败。这里固定住“每条结果只用它自己冻结的历史与 compositionId 归因”。
/// </remarks>
public sealed class CommitSnapshotSlicerTests
{
    private static CommitSnapshot Snapshot(string result, int compositionId, params string[] preedit)
        => new(result, compositionId, preedit);

    [Fact]
    public void TwoIndependentCompositionSessions_EachKeepsItsOwnHistoryAndVerdict()
    {
        // 第 1 段：nihao -> 你好；第 2 段（新 start）：ma -> 吗。
        // 第 2 段的 start 会清空当前历史，但第 1 条结果已冻结自己的历史。
        var first = Snapshot("你好", 1, "n", "ni", "ni'h", "ni'hao");
        var second = Snapshot("吗", 2, "m", "ma");
        var newSnapshots = CommitSnapshotSlicer.TakeNewResults([first, second], alreadyRecorded: 0);

        var firstVerdict = CommitSnapshotSlicer.EvaluateFirstNew(newSnapshots, "你好", true, "nihao");
        var secondVerdict = CommitSnapshotSlicer.EvaluateSecondNew(newSnapshots, "吗", true, "ma");

        Assert.True(firstVerdict.Satisfied, firstVerdict.Reason);
        Assert.True(firstVerdict.PinyinSeenInPreedit);
        Assert.True(secondVerdict.Satisfied, secondVerdict.Reason);
        Assert.True(secondVerdict.PinyinSeenInPreedit);
        Assert.Equal(1, newSnapshots[0].CompositionId);
        Assert.Equal(2, newSnapshots[1].CompositionId);
    }

    [Fact]
    public void SecondStartDoesNotClearTheAlreadyRecordedFirstResult()
    {
        // 模拟“第一段被记录后，第二段开始”：切片里第一条仍然是它自己的历史。
        var first = Snapshot("你好", 1, "n", "ni", "ni'hao");
        var second = Snapshot("吗", 2, "m", "ma");
        var newSnapshots = CommitSnapshotSlicer.TakeNewResults([first, second], alreadyRecorded: 0);

        Assert.Equal(["n", "ni", "ni'hao"], newSnapshots[0].PreeditHistory);
        Assert.DoesNotContain("ma", newSnapshots[0].PreeditHistory);
        Assert.True(CommitSnapshotSlicer.EvaluateFirstNew(newSnapshots, "你好", true, "nihao").Satisfied);
    }

    [Fact]
    public void FreezeResultAndResetHistory_CopiesHistoryAndClearsTheSourceList()
    {
        var currentHistory = new List<string> { "n", "ni", "ni'h", "ni'hao" };

        var snapshot = CommitSnapshotSlicer.FreezeResultAndResetHistory("你好", 1, currentHistory);

        Assert.Equal(["n", "ni", "ni'h", "ni'hao"], snapshot.PreeditHistory);
        Assert.Empty(currentHistory);

        // 复制语义：之后修改源集合不会改写已冻结的快照。
        currentHistory.Add("ma");
        Assert.Equal(["n", "ni", "ni'h", "ni'hao"], snapshot.PreeditHistory);
    }

    [Fact]
    public void SameMessageResultPlusFollowingPreedit_SecondSnapshotDoesNotContainPreviousSegment()
    {
        // 真实生产路径：冻结时复制并清空当前段历史，后续 compStr("ma") 不会与上一段 nihao 混在一起。
        var currentHistory = new List<string> { "n", "ni", "ni'h", "ni'hao" };

        var first = CommitSnapshotSlicer.FreezeResultAndResetHistory("你好", 1, currentHistory);

        // 同一段 message 的“后续预编辑”追加进已经清空的历史（下一段没有新的 WM_IME_STARTCOMPOSITION）。
        currentHistory.Add("m");
        currentHistory.Add("ma");
        var second = CommitSnapshotSlicer.FreezeResultAndResetHistory("吗", 1, currentHistory);
        var newSnapshots = CommitSnapshotSlicer.TakeNewResults([first, second], alreadyRecorded: 0);

        // 第一段快照仍保有 nihao；第二段只含 ma，绝不含 nihao。
        Assert.Equal(["n", "ni", "ni'h", "ni'hao"], newSnapshots[0].PreeditHistory);
        Assert.Equal(["m", "ma"], newSnapshots[1].PreeditHistory);
        Assert.DoesNotContain("ni'hao", newSnapshots[1].PreeditHistory);
        Assert.Empty(currentHistory);

        Assert.True(CommitSnapshotSlicer.EvaluateFirstNew(newSnapshots, "你好", true, "nihao").Satisfied);
        Assert.True(CommitSnapshotSlicer.EvaluateSecondNew(newSnapshots, "吗", true, "ma").Satisfied);
        // 若历史没有被重置，第二段会被误判成“nihao 可归因”，这里固定住它必须失败。
        Assert.False(CommitSnapshotSlicer.EvaluateSecondNew(newSnapshots, "吗", true, "nihao").Satisfied);
    }

    [Fact]
    public void NewResultWithoutPreeditHistory_FailsEvenWhenContentMatches()
    {
        var onlyResult = Snapshot("你好", 1);
        var newSnapshots = CommitSnapshotSlicer.TakeNewResults([onlyResult], alreadyRecorded: 0);

        var verdict = CommitSnapshotSlicer.EvaluateFirstNew(newSnapshots, "你好", true, "nihao");

        Assert.False(verdict.Satisfied);
        Assert.False(verdict.PinyinSeenInPreedit);
    }

    [Fact]
    public void PreviousScenarioResults_AreExcludedFromTheNewSlice()
    {
        // 上一个场景已经记录了 1 条结果；本场景只应看到之后新增的那条。
        var fromPreviousScenario = Snapshot("旧结果", 7, "jiu");
        var current = Snapshot("你好", 8, "n", "ni", "ni'hao");

        var newSnapshots = CommitSnapshotSlicer.TakeNewResults([fromPreviousScenario, current], alreadyRecorded: 1);

        Assert.Single(newSnapshots);
        Assert.Equal("你好", newSnapshots[0].Result);
        Assert.Equal(8, newSnapshots[0].CompositionId);
        Assert.True(CommitSnapshotSlicer.EvaluateFirstNew(newSnapshots, "你好", true, "nihao").Satisfied);
        // 第二条不存在：必须报缺失，而不是回退到上场景的结果。
        Assert.False(CommitSnapshotSlicer.EvaluateSecondNew(newSnapshots, "吗", true, "ma").Satisfied);
    }

    [Fact]
    public void TakeNewResults_ReturnsEmptyWhenNothingNewArrived()
    {
        var existing = Snapshot("你好", 1, "ni'hao");

        Assert.Empty(CommitSnapshotSlicer.TakeNewResults([existing], alreadyRecorded: 1));
    }

    [Fact]
    public void MissingSnapshot_ReportsFailureInsteadOfPassing()
    {
        Assert.False(CommitSnapshotSlicer.EvaluateSnapshot(null, "你好", true, "nihao").Satisfied);
        Assert.False(CommitSnapshotSlicer.EvaluateFirstNew([], "你好", true, "nihao").Satisfied);
        Assert.False(CommitSnapshotSlicer.EvaluateSecondNew([], "吗", true, "ma").Satisfied);
    }
}
