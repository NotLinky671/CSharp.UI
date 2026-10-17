namespace CSharp.UI.ImeProbe;

/// <summary>
/// 一条 <c>GCS_RESULTSTR</c> 提交在记录时刻冻结的现场快照。
/// </summary>
/// <param name="Result">本条消息读到的结果串。</param>
/// <param name="CompositionId">该结果所属的组合会话序号。</param>
/// <param name="PreeditHistory">
/// 该结果所属组合会话的预编辑历史（记录时刻的副本）。同一段 message 里“结果 + 后续预编辑”的后续串
/// 属于下一段，不并入这里。
/// </param>
internal sealed record CommitSnapshot(string Result, int CompositionId, IReadOnlyList<string> PreeditHistory);

/// <summary>
/// 从已冻结的逐结果快照里取出“本场景新增的结果”，并把每条结果与它<b>自己</b>的预编辑历史配对判定。
/// </summary>
/// <remarks>
/// <para>
/// 这是纯托管逻辑，可用确定性测试覆盖。存在的理由是一个真实缺陷：分两段提交时，第二段的组合会话会让
/// 当前预编辑历史只剩第二段，如果拿“最后的当前历史”去判定第一段结果，第一段必然失败。
/// </para>
/// <para>
/// 归因必须按结果切片：每条结果携带它记录时刻的快照（含所属 compositionId 与该会话的预编辑历史），
/// 判定只看这条结果自己的历史。
/// </para>
/// </remarks>
internal static class CommitSnapshotSlicer
{
    /// <summary>
    /// 冻结一条结果并<b>重置当前段的预编辑历史</b>：先把 <paramref name="currentHistory"/> 复制为快照，
    /// 再清空原集合，最后返回快照。
    /// </summary>
    /// <remarks>
    /// 复制只防止“过去被改写”，不防止“未来被污染”：同一段 message 若带“结果 + 后续预编辑”，
    /// 后续 <c>compStr</c> 属于下一段；而下一段不一定有新的 <c>WM_IME_STARTCOMPOSITION</c>。
    /// 因此记录结果时就必须清空当前段历史，否则上一段的 nihao 会被带进下一段结果的快照。
    /// </remarks>
    internal static CommitSnapshot FreezeResultAndResetHistory(
        string result, int compositionId, List<string> currentHistory)
    {
        // ToArray 是复制：快照持有记录时刻的内容，之后 currentHistory 的增删都不影响它。
        var snapshot = new CommitSnapshot(result, compositionId, currentHistory.ToArray());
        currentHistory.Clear();
        return snapshot;
    }

    /// <summary>本场景新增的结果（相对场景起点快照数量）。</summary>
    internal static IReadOnlyList<CommitSnapshot> TakeNewResults(
        IReadOnlyList<CommitSnapshot> allSnapshots, int alreadyRecorded)
    {
        if (allSnapshots.Count <= alreadyRecorded)
        {
            return [];
        }

        var result = new List<CommitSnapshot>(allSnapshots.Count - alreadyRecorded);
        for (var i = alreadyRecorded; i < allSnapshots.Count; i++)
        {
            result.Add(allSnapshots[i]);
        }

        return result;
    }

    /// <summary>
    /// 判定单条结果：使用该结果自己冻结的预编辑历史与 compositionId。
    /// </summary>
    internal static CommitContentVerdict EvaluateSnapshot(
        CommitSnapshot? snapshot, string? expectedResult, bool requireChinese, string? expectedPinyin)
        => snapshot is null
            ? new CommitContentVerdict(false, false, false, "no commit snapshot for this result")
            : CommitOutcome.Evaluate(
                snapshot.Result, expectedResult, requireChinese, expectedPinyin, snapshot.PreeditHistory);

    /// <summary>判定第一条结果（按本场景新增切片）。</summary>
    internal static CommitContentVerdict EvaluateFirstNew(
        IReadOnlyList<CommitSnapshot> newSnapshots, string? expectedResult, bool requireChinese, string? expectedPinyin)
        => EvaluateSnapshot(
            newSnapshots.Count >= 1 ? newSnapshots[0] : null, expectedResult, requireChinese, expectedPinyin);

    /// <summary>判定第二条结果（按本场景新增切片）。</summary>
    internal static CommitContentVerdict EvaluateSecondNew(
        IReadOnlyList<CommitSnapshot> newSnapshots, string? expectedResult, bool requireChinese, string? expectedPinyin)
        => EvaluateSnapshot(
            newSnapshots.Count >= 2 ? newSnapshots[1] : null, expectedResult, requireChinese, expectedPinyin);
}
