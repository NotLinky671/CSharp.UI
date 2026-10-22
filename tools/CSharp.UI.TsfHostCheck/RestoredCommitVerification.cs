namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R37 opt-in 提交证明的<b>唯一期望值</b>：恢复后的当前候选（页内选中「你好」）必须能被
/// 一次真实 Space 提交为文档文本「你好」，绑定记录恰好新增一条 <c>tsf-composition</c> 写入。
/// </summary>
internal static class RestoredCommitExpectation
{
    /// <summary>提交后文档的精确文本（UTF-16 逐码元相等，不做包含/前缀比较）。</summary>
    internal const string ExpectedText = "你好";

    /// <summary>提交步骤内的绑定写入增量必须精确为 1（0 = 没提交，2 = 重复提交）。</summary>
    internal const int ExpectedWrites = 1;

    /// <summary>唯一允许的写入来源标签（组合结束的非空最终文本）。</summary>
    internal const string ExpectedSource = "tsf-composition";

    /// <summary>等待组合结束的上限（有界）。</summary>
    internal const int CommitWaitMs = 3_000;

    /// <summary>组合结束后的稳定窗口：窗口内再出现任何写入即判失败。</summary>
    internal const int StableWindowMs = 400;

    /// <summary>真实 Space 的按键标签（日志/守卫拒绝原因对齐用）。</summary>
    internal const string KeyLabel = "restored-commit";

    /// <summary>提交后可选 root 证据图文件名（不含扩展名）。</summary>
    internal const string PostCommitImageName = "r37-restored-commit";
}

/// <summary>
/// 提交前在<b>单个 UI 安全点</b>内读到的真实事实。全部字段都是实际读数：
/// 安全点（含 profile COM 查询）结果、与执行器几何基线的不变量差异、store 快照、
/// presenter 当前快照（id/代数/页内选中文本）、overlay 真实可见性。
/// 生产路径由 <c>ScenarioRunner</c> 填充，测试注入同样形状的替身事实——判定是同一个。
/// </summary>
internal readonly record struct RestoredCommitPreFacts(
    bool SafePointOk,
    string SafePointDetail,
    string? InvariantViolation,
    string Text,
    int SpanStart,
    int SpanEnd,
    int Writes,
    /// <summary>同一安全点内读到的<b>绝对</b>绑定记录条数——提交后据此取增量条目，
    /// 与 <see cref="Writes"/>（相对写入增量）同一次读取，不跨两次派发拼接。</summary>
    int AbsoluteWrites,
    bool Composing,
    string Profile,
    int BeginCount,
    int EndCount,
    int SessionId,
    int FocusEvents,
    bool PresenterAvailable,
    bool PresenterValid,
    uint ElementId,
    uint Generation,
    int EntryCount,
    string? SelectedText,
    int SelectionOnPage,
    bool OverlayInspectOk,
    bool OverlayVisible,
    uint OverlayElementId,
    uint OverlayGeneration,
    string? OverlayFailure)
{
    internal string Describe()
        => $"safe_point={(SafePointOk ? "ok" : SafePointDetail)} "
            + $"text='{Text}' span={SpanStart}..{SpanEnd} writes={Writes} composing={Composing} "
            + $"presenter={(PresenterAvailable ? (PresenterValid ? "valid" : "invalid") : "missing")} "
            + $"id={ElementId} gen={Generation} entries={EntryCount} "
            + $"sel_on_page={SelectionOnPage} selected='{SelectedText ?? "(none)"}' "
            + $"overlay={(OverlayInspectOk ? (OverlayVisible ? "visible" : "hidden") : "inspect-failed")} "
            + $"overlay_id={OverlayElementId} overlay_gen={OverlayGeneration} "
            + $"begin={BeginCount} end={EndCount} session={SessionId} focus_events={FocusEvents} "
            + $"profile='{Profile}'";
}

/// <summary>
/// 提交后（有界等待 + 稳定窗口之后）读到的真实事实：组合是否真的结束、结束次数、
/// 文档文本、绑定写入增量与实际条目（文本/来源）、presenter 是否清空、overlay 真实可见性、
/// 以及提交后的严格安全点结果（profile 仍必须可核验）。
/// </summary>
internal readonly record struct RestoredCommitPostFacts(
    bool SafePointOk,
    string SafePointDetail,
    string Text,
    int Writes,
    bool Composing,
    int BeginCount,
    int EndCount,
    int SessionId,
    int FocusEvents,
    string Profile,
    int WriteDelta,
    IReadOnlyList<BindingWrite>? NewWrites,
    bool CompositionEnded,
    bool PresenterCleared,
    bool OverlayInspectOk,
    bool OverlayVisible,
    int? WritesAtEndOfWait)
{
    internal string DescribeEntries()
    {
        if (NewWrites is null || NewWrites.Count == 0)
        {
            return "(none)";
        }

        return string.Join(" | ", NewWrites.Select((w, i) =>
            $"{i}:text='{w.Text}' source={w.Source} version={w.Version} seq={w.Sequence}"));
    }

    internal string Describe()
        => $"safe_point={(SafePointOk ? "ok" : SafePointDetail)} "
            + $"ended={CompositionEnded} composing={Composing} "
            + $"text='{Text}' writes_delta={WriteDelta} "
            + $"entries=[{DescribeEntries()}] "
            + $"begin={BeginCount} end={EndCount} session={SessionId} "
            + $"focus_events={FocusEvents} profile='{Profile}' "
            + $"presenter_cleared={PresenterCleared} "
            + $"overlay={(OverlayInspectOk ? (OverlayVisible ? "visible" : "hidden") : "inspect-failed")} "
            + $"writes_at_end_of_wait={(WritesAtEndOfWait?.ToString() ?? "(not-sampled)")}";
}

/// <summary>
/// R37：提交前/后事实的<b>共享判定</b>——生产 <c>ScenarioRunner</c> 与单元测试调用同一个
/// 谓词，不是"测试里另写一套"。任何一条不满足都返回可读原因（null = 通过）；
/// 判定只看真实读数，绝不因为"某个诊断属性为 true"就放行。
/// </summary>
internal static class RestoredCommitGate
{
    /// <summary>
    /// 提交前门禁：严格安全点成立、组合不变量与几何基线一致、store 零写入且仍在组合中、
    /// presenter 有有效快照（非零代数）、页内选中文本精确为「你好」、overlay 真实可见。
    /// </summary>
    internal static string? EvaluatePre(in RestoredCommitPreFacts pre)
    {
        if (!pre.SafePointOk)
        {
            return $"pre safe point failed: {pre.SafePointDetail}";
        }

        if (pre.InvariantViolation is { } violation)
        {
            return $"pre composition invariant violated: {violation}";
        }

        if (!pre.Composing)
        {
            return "composition is not active before commit";
        }

        if (pre.Writes != 0)
        {
            return $"binding writes before commit must be 0 (got {pre.Writes})";
        }

        if (!pre.PresenterAvailable)
        {
            return "host-drawn presenter is not wired";
        }

        if (!pre.PresenterValid)
        {
            return "presenter has no valid published snapshot before commit";
        }

        if (pre.Generation == 0)
        {
            return "presenter generation is zero (no live element)";
        }

        if (pre.EntryCount == 0)
        {
            return "presenter snapshot has no candidates before commit";
        }

        if (pre.SelectionOnPage < 0)
        {
            return "presenter snapshot has no page-local selection before commit";
        }

        if (!string.Equals(pre.SelectedText, RestoredCommitExpectation.ExpectedText,
                StringComparison.Ordinal))
        {
            return $"selected candidate is not '{RestoredCommitExpectation.ExpectedText}' "
                + $"(got '{pre.SelectedText ?? "(none)"}')";
        }

        if (!pre.OverlayInspectOk)
        {
            return $"overlay inspection failed before commit: {pre.OverlayFailure ?? "unknown"}";
        }

        if (!pre.OverlayVisible)
        {
            return "restored candidate overlay is not really visible before commit";
        }

        // 展示来源必须就是 presenter 的<b>当前</b>快照（同 id 同代数）——与既有锚定
        // 门禁同一条事实，绝不放宽成"overlay 亮着就算"。
        if (pre.OverlayElementId != pre.ElementId || pre.OverlayGeneration != pre.Generation)
        {
            return $"overlay is not displaying the current presenter snapshot "
                + $"(overlay id={pre.OverlayElementId} gen={pre.OverlayGeneration}, "
                + $"presenter id={pre.ElementId} gen={pre.Generation})";
        }

        return null;
    }

    /// <summary>
    /// 提交后门禁：组合恰好结束一次（end +1、begin/session 不变、焦点事件不变）、
    /// 文档文本精确「你好」、绑定增量精确 1 且该条目文本/来源正确、稳定窗口内无额外写入、
    /// presenter 已清空且 overlay 真实不可见、提交后严格安全点（含 profile）仍成立。
    /// </summary>
    internal static string? EvaluatePost(in RestoredCommitPreFacts pre,
        in RestoredCommitPostFacts post)
    {
        if (!post.CompositionEnded)
        {
            return $"composition did not end within {RestoredCommitExpectation.CommitWaitMs}ms";
        }

        if (post.Composing)
        {
            return "composition is still active after commit";
        }

        if (post.EndCount != pre.EndCount + 1)
        {
            return $"composition end count must increase exactly once "
                + $"(before={pre.EndCount} after={post.EndCount})";
        }

        if (post.BeginCount != pre.BeginCount)
        {
            return $"composition begin count changed during commit "
                + $"(before={pre.BeginCount} after={post.BeginCount}) — composition restarted";
        }

        if (post.SessionId != pre.SessionId)
        {
            return $"composition session id changed during commit "
                + $"(before={pre.SessionId} after={post.SessionId}) — a new composition started";
        }

        if (post.FocusEvents != pre.FocusEvents)
        {
            return $"input focus events changed during commit "
                + $"(before={pre.FocusEvents} after={post.FocusEvents}) — focus left and returned";
        }

        if (!string.Equals(post.Text, RestoredCommitExpectation.ExpectedText,
                StringComparison.Ordinal))
        {
            return $"document text is not exactly '{RestoredCommitExpectation.ExpectedText}' "
                + $"(got '{post.Text}')";
        }

        if (post.WriteDelta != RestoredCommitExpectation.ExpectedWrites)
        {
            return $"binding write delta must be exactly {RestoredCommitExpectation.ExpectedWrites} "
                + $"(got {post.WriteDelta})";
        }

        if (post.NewWrites is null
            || post.NewWrites.Count != RestoredCommitExpectation.ExpectedWrites)
        {
            return $"recorded binding entries must be exactly "
                + $"{RestoredCommitExpectation.ExpectedWrites} "
                + $"(got {post.NewWrites?.Count ?? 0})";
        }

        var entry = post.NewWrites[0];
        if (!string.Equals(entry.Text, RestoredCommitExpectation.ExpectedText,
                StringComparison.Ordinal))
        {
            return $"recorded binding entry text is not '{RestoredCommitExpectation.ExpectedText}' "
                + $"(got '{entry.Text}')";
        }

        if (!string.Equals(entry.Source, RestoredCommitExpectation.ExpectedSource,
                StringComparison.Ordinal))
        {
            return $"recorded binding entry source is not "
                + $"'{RestoredCommitExpectation.ExpectedSource}' (got '{entry.Source}')";
        }

        if (post.WritesAtEndOfWait is { } atEnd && atEnd != post.WriteDelta)
        {
            return $"an additional write landed while the commit was settling "
                + $"(delta {post.WriteDelta} -> {atEnd})";
        }

        if (!post.PresenterCleared)
        {
            return "presenter still holds a published snapshot after commit";
        }

        if (!post.OverlayInspectOk)
        {
            return "overlay inspection failed after commit";
        }

        if (post.OverlayVisible)
        {
            return "candidate overlay is still really visible after commit";
        }

        if (!post.SafePointOk)
        {
            return $"post safe point failed: {post.SafePointDetail}";
        }

        return null;
    }

    /// <summary>
    /// 提交后可选证据图的门禁：只核对<b>已提交状态是否保持</b>——文本精确、写入增量 1、
    /// 不在组合中、presenter 已清空、overlay 真实不可见、严格安全点仍成立。
    /// 刻意<b>不</b>复用组合中的旧基线门禁（那要求 Composing==true，提交后必然失败），
    /// 也不放宽任何既有门禁——本判定只用于新增的可选证据图。
    /// </summary>
    internal static string? EvaluateCommittedStatePreserved(in RestoredCommitPostFacts post)
    {
        if (!post.SafePointOk)
        {
            return $"committed-state safe point failed: {post.SafePointDetail}";
        }

        if (post.Composing)
        {
            return "committed state changed: composition is active again";
        }

        if (!string.Equals(post.Text, RestoredCommitExpectation.ExpectedText,
                StringComparison.Ordinal))
        {
            return $"committed text changed (got '{post.Text}')";
        }

        if (post.WriteDelta != RestoredCommitExpectation.ExpectedWrites)
        {
            return $"committed write delta changed (got {post.WriteDelta})";
        }

        if (!post.PresenterCleared)
        {
            return "committed state changed: presenter published a snapshot again";
        }

        if (!post.OverlayInspectOk)
        {
            return "committed-state overlay inspection failed";
        }

        if (post.OverlayVisible)
        {
            return "committed state changed: candidate overlay is visible again";
        }

        return null;
    }
}
