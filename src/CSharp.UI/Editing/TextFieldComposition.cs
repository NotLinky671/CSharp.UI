namespace CSharp.UI;

/// <summary>
/// 真实 <see cref="TextField"/> 的组合状态机（TI-04）：预编辑不写 Binding，分段结果只经真实 TI-03 事务提交一次。
/// </summary>
/// <remarks>
/// <para>
/// <b>归属与顺序：</b>焦点 epoch 由运行时统一分配（同一运行时的不同节点不复用）。组合 ID 在 epoch 内<b>严格递增</b>
/// （允许跳号），因此“≤ 已见水位”的旧组合重放一律被拒绝，不需要保留无界的已结束 ID 集合，也不需要近期 ID 环形
/// 缓冲。序号在同一组合内跨消息递增，用单调水位拒绝重复。
/// </para>
/// <para>
/// <b>版本锚点：</b>文档版本一律以真实回读为准。Start/Update 必须锚定当前真实版本；活动段的结果锚定该段起始版本；
/// <b>没有 Start 的 Result</b>（真实平台存在）按事件自带的 <see cref="CompositionEvent.DocumentVersion"/> 精确锚定，
/// 并使用事件显式的 <see cref="CompositionEvent.Replacement"/>，绝不按最新 caret 猜测。
/// </para>
/// <para>
/// <b>拒绝无副作用：</b>所有身份/版本/负载校验都在提交状态之前完成；被拒绝的事件不消耗组合 ID、不推进序号水位、
/// 不改预编辑。真正属于“本段的结果但事务未落地”（容量拒绝、setter 拒绝、净化后无变化、空操作）的事件会被消费
/// 且退休其序号，保证同一事件不会再次调用绑定 setter。
/// </para>
/// <para>
/// <b>不重入布局：</b>本类型只读写文档与选区、调用注入的提交委托；不执行布局、不生成帧、不调用任何平台接口。
/// </para>
/// </remarks>
internal sealed class TextFieldComposition
{
    /// <summary>已接受的最高组合 ID；等于它的组合必须已经结束，因此可用于拒绝旧重放。</summary>
    private int _highestCompositionId = -1;

    /// <summary>最近结束的组合 ID；只用于“同一身份重复 Cancel/End 幂等”，不保留历史集合。</summary>
    private int _lastEndedCompositionId = -1;

    /// <summary>当前是否正在组合（有未提交段）。</summary>
    public bool IsComposing { get; private set; }

    /// <summary>当前未提交段的锚点；无活动段时为 null。</summary>
    public CompositionAnchor? Active { get; private set; }

    /// <summary>当前组合 ID（活动段或最近结束段的身份）。</summary>
    public int CompositionId { get; private set; }

    /// <summary>当前组合已接受的最高序号（单调水位）。</summary>
    public int LastSequence { get; private set; }

    /// <summary>最近一次真实提交的事务结果；没有提交过时为 <c>default</c>（Status=None）。</summary>
    public EditResult LastCommit { get; private set; }

    /// <summary>成功提交的次数（真实写入绑定的事务次数）。</summary>
    public int CommitCount { get; private set; }

    /// <summary>最近一次应用后的真实文档快照（诊断用）。</summary>
    public TextSnapshot LastDocument => new(_documentText, _lastSelection, _lastVersion);

    /// <summary>最近一次结果写入是否因绑定 setter/getter 抛错而失败（供客户端记录脱敏诊断）。</summary>
    public bool LastResultWriteFailed { get; private set; }

    /// <summary>当前预编辑串；无组合时为空串。</summary>
    public string Preedit => Active is { } active ? active.Preedit : string.Empty;

    /// <summary>当前显示串：<c>已提交前缀 + 预编辑 + 已提交后缀</c>。</summary>
    /// <remarks>预编辑整体替换活动段的替换区；已确认的前缀/后缀始终保留。预编辑不进入绑定、版本或历史。</remarks>
    public string DisplayString
        => Active is { } active
            ? _documentText[..Math.Clamp(active.Replacement.Start, 0, _documentText.Length)]
                + active.Preedit
                + _documentText[Math.Clamp(active.Replacement.End, 0, _documentText.Length)..]
            : _documentText;

    /// <summary>当前预编辑 caret（相对预编辑串的 UTF-16 偏移）；无组合时为 0。</summary>
    public int PreeditCaret => Active is { } active ? active.Caret : 0;

    /// <summary>当前预编辑分段（不可变快照）；无组合时为空。</summary>
    public IReadOnlyList<TextRange> Segments => Active is { } active ? active.Segments : [];

    private int _segmentVersion;
    private string _documentText = string.Empty;
    private TextSelection _lastSelection;
    private int _lastVersion;
    private object? _contextIdentity;
    private int _contextMaxLength = int.MaxValue;
    private CompositionDocument? _lastReliableDocument;

    /// <summary>
    /// 应用一个组合事件批次，返回<b>实际完成</b>的动作与真实文档快照。
    /// </summary>
    /// <param name="events">有序组合事件批次。</param>
    /// <param name="commit">把一段结果作为一次真实 TI-03 事务提交。</param>
    /// <param name="readDocument">
    /// 读取当前真实文档视图（文本、版本、选区、来源身份、MaxLength）。状态机<b>在每个事件之前</b>重新调用它，
    /// 因此外部绑定变化、以及 setter 内同步换源/换配置/卸载/换焦点都会在下一个事件执行前被发现；
    /// 实现方保证不递归布局、不产生平台副作用，并且自身不抛异常（读取失败时返回最后可靠快照）。
    /// </param>
    /// <param name="isAuthorized">
    /// 当前客户端在批次处理期间是否仍是有效归属；每个事件之前重新求值，批次中途被撤销时剩余事件显式拒绝，
    /// 但已经执行的写入前缀照常如实报告。
    /// </param>
    /// <remarks>
    /// <para>
    /// 批次按“先结果、后预编辑”的顺序逐事件处理；已处理的前缀不会被回滚。第一个被拒绝的事件会终止本批次的
    /// 后续处理；返回的动作集合与 <see cref="TextInputApplyResult.AppliedEventCount"/> 始终反映真实已执行的部分，
    /// 返回的文档快照是处理结束后的<b>真实</b>文本/版本/选区。
    /// </para>
    /// <para>本方法不抛异常；原生回调路径不允许托管异常越界。</para>
    /// </remarks>
    public TextInputApplyResult Apply(
        IReadOnlyList<CompositionEvent>? events,
        int expectedSessionId,
        CompositionCommit commit,
        Func<CompositionDocument> readDocument,
        Func<bool>? isAuthorized = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(readDocument);

        if (events is null || events.Count == 0)
        {
            return TextInputApplyResult.NotHandled(LastDocument, "组合事件批次为空；没有需要应用的内容。");
        }

        var outcome = TextInputOutcome.None;
        var applied = 0;
        string? rejection = null;
        LastResultWriteFailed = false;
        var document = ReadDocument(readDocument);

        for (var i = 0; i < events.Count; i++)
        {
            // 每个事件之前都重新回读真实文档并重新校验归属：外部文本/来源/MaxLength 变化，
            // 以及上一个事件的 setter 内同步换源/卸载/换焦点，都会在下一个事件执行前被发现。
            document = ReadDocument(readDocument);

            if (isAuthorized is not null && !isAuthorized())
            {
                rejection = "组合批次处理期间焦点归属、文档来源或客户端状态已变化；剩余事件不写入。";
                outcome |= TextInputOutcome.Rejected;
                break;
            }

            Step step;
            try
            {
                step = ApplyOne(events[i], expectedSessionId, commit, document, out var updated);
                document = updated;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 原生回调路径不允许托管异常越界：真实已执行的前缀照常保留，事件已消费。
                _ = ex;
                LastResultWriteFailed = true;
                rejection = "组合事件应用失败（绑定读取/事务异常）；已执行的动作如实保留。";
                outcome |= TextInputOutcome.Rejected;
                break;
            }

            if (step.Rejected)
            {
                rejection = step.Reason;
                outcome |= TextInputOutcome.Rejected;
                break;
            }

            outcome |= step.Outcome;
            applied++;
        }

        // 返回快照必须等于提交/取消后的真实文档（文本、版本、选区），而不是事件前的锚点。
        document = ReadDocument(readDocument);

        var handled = applied > 0;
        return new TextInputApplyResult(outcome, document.Snapshot, handled, rejection, applied);
    }

    /// <summary>读取真实文档；实现方抛出时退回最后一次可靠快照，绝不让异常越过原生回调。</summary>
    private CompositionDocument ReadDocument(Func<CompositionDocument> readDocument)
    {
        try
        {
            var document = readDocument();
            SyncDocument(document);
            return document;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
            return _lastReliableDocument
                ?? new CompositionDocument(_documentText, _lastVersion, _lastSelection, _contextIdentity ?? this, _contextMaxLength);
        }
    }

    private Step ApplyOne(
        CompositionEvent e,
        int expectedSessionId,
        CompositionCommit commit,
        CompositionDocument document,
        out CompositionDocument updated)
    {
        updated = document;

        if (e.CompositionSessionId != expectedSessionId)
        {
            return Step.Fail($"事件 epoch {e.CompositionSessionId} 与当前客户端 epoch {expectedSessionId} 不一致；旧会话事件不写入。");
        }

        if (!Enum.IsDefined(e.Kind))
        {
            return Step.Fail($"组合事件种类 {(int)e.Kind} 未定义；未知事件类型不写入。");
        }

        if (e.CompositionSessionId < 0 || e.CompositionId < 0 || e.Sequence < 0 || e.DocumentVersion < 0)
        {
            return Step.Fail("组合事件的会话/组合/序号/版本出现负值；身份非法，不写入。");
        }

        return e.Kind switch
        {
            CompositionEventKind.Start => Start(e, document, out updated),
            CompositionEventKind.Update => Update(e, document),
            CompositionEventKind.Result => Result(e, commit, document, out updated),
            CompositionEventKind.Cancel => Cancel(e, document, out updated),
            _ => End(e, document, out updated),
        };
    }

    // ---------------------------------------------------------------- Start

    private Step Start(CompositionEvent e, CompositionDocument document, out CompositionDocument updated)
    {
        updated = document;

        if (IsComposing)
        {
            return Step.Fail($"该焦点客户端已有活动组合 {CompositionId}；重复 Start 不写入。");
        }

        if (e.CompositionId <= _highestCompositionId)
        {
            return Step.Fail($"组合 ID {e.CompositionId} 不高于已见水位 {_highestCompositionId}；旧组合重放不写入。");
        }

        if (e.DocumentVersion != document.Version)
        {
            return Step.Fail($"Start 版本锚点 v{e.DocumentVersion} 与真实文档 v{document.Version} 不一致；过时或未来事件不写入。");
        }

        if (!TryValidatePreeditPayload(e.Preedit, e.CompositionCaret, e.Segments, out var shapeError))
        {
            return Step.Fail(shapeError!);
        }

        if (!e.TryValidatePayload(document.Snapshot, out var payloadError))
        {
            return Step.Fail(payloadError!);
        }

        // 全部校验通过后才提交身份与状态：无效 Start 不消耗组合 ID。
        _highestCompositionId = e.CompositionId;
        CompositionId = e.CompositionId;
        LastSequence = e.Sequence;

        var selection = e.SelectionBeforeComposition is { } before
            ? before.Clamp(document.Length)
            : document.Snapshot.Selection;

        Active = new CompositionAnchor(
            e.CompositionId,
            e.DocumentVersion,
            e.Replacement,
            selection,
            e.Preedit,
            e.CompositionCaret ?? e.Preedit.Length,
            FreezeSegments(e.Segments));
        IsComposing = true;
        _segmentVersion = document.Version;
        _documentText = document.Text;
        _lastSelection = selection;
        _lastVersion = document.Version;
        _contextIdentity = document.Identity;
        _contextMaxLength = document.MaxLength;
        return Step.Ok(TextInputOutcome.Started);
    }

    // ---------------------------------------------------------------- Update

    private Step Update(CompositionEvent e, CompositionDocument document)
    {
        if (!IsComposing)
        {
            // 没有 Start 的 Update 缺少替换区与选区锚点：拒绝，不按最新 caret 猜测范围。
            return Step.Fail("没有活动组合的 Update 被拒绝：缺少 Start 的替换区/选区锚点，不能按最新 caret 猜测范围。");
        }

        if (e.CompositionId != CompositionId)
        {
            return Step.Fail($"组合 ID {e.CompositionId} 与活动组合 {CompositionId} 不一致；跨组合事件不写入。");
        }

        if (e.Sequence <= LastSequence)
        {
            return Step.Fail($"组合 {e.CompositionId} 的序号 {e.Sequence} 未递增（已接受 {LastSequence}）；重复或乱序事件不写入。");
        }

        if (e.DocumentVersion != _segmentVersion)
        {
            return Step.Fail($"Update 版本锚点 v{e.DocumentVersion} 与活动段版本 v{_segmentVersion} 不一致；过时或未来事件不写入。");
        }

        if (!MatchesContext(document))
        {
            return Step.Fail("组合期间绑定来源或 MaxLength 已变化；该段预编辑不再有效，必须取消后重开。");
        }

        if (!TryValidatePreeditPayload(e.Preedit, e.CompositionCaret, e.Segments, out var shapeError))
        {
            return Step.Fail(shapeError!);
        }

        if (!TryValidateReplacement(Active!.Value.Replacement, document, out var rangeError))
        {
            return Step.Fail(rangeError!);
        }

        LastSequence = e.Sequence;
        Active = Active.Value with
        {
            Preedit = e.Preedit,
            Caret = e.CompositionCaret ?? e.Preedit.Length,
            Segments = FreezeSegments(e.Segments),
        };
        return Step.Ok(TextInputOutcome.Updated);
    }

    // ---------------------------------------------------------------- Result

    private Step Result(
        CompositionEvent e,
        CompositionCommit commit,
        CompositionDocument document,
        out CompositionDocument updated)
    {
        updated = document;

        if (!TryValidateCommittedText(e.Preedit, out var textError))
        {
            return Step.Fail(textError!);
        }

        if (e.NextPreedit is { } next && !TryValidatePreeditPayload(next, null, null, out var nextError))
        {
            return Step.Fail(nextError!);
        }

        var hasActive = IsComposing && e.CompositionId == CompositionId;
        if (IsComposing && !hasActive)
        {
            return Step.Fail($"组合 ID {e.CompositionId} 与活动组合 {CompositionId} 不一致；结果不写入。");
        }

        TextRange replacement;
        if (hasActive)
        {
            if (e.Sequence <= LastSequence)
            {
                return Step.Fail($"组合 {e.CompositionId} 的序号 {e.Sequence} 未递增（已接受 {LastSequence}）；重复或乱序结果不写入。");
            }

            if (e.DocumentVersion != _segmentVersion)
            {
                return Step.Fail($"结果版本锚点 v{e.DocumentVersion} 与活动段版本 v{_segmentVersion} 不一致；过时或未来事件不写入。");
            }

            if (!MatchesContext(document))
            {
                return Step.Fail("组合期间绑定来源或 MaxLength 已变化；结果不写入。");
            }

            replacement = Active!.Value.Replacement;
        }
        else
        {
            // 没有 Start 的 Result 序列（真实平台存在）：精确锚定事件自带版本与显式替换范围。
            if (e.CompositionId <= _highestCompositionId)
            {
                return Step.Fail($"组合 ID {e.CompositionId} 不高于已见水位 {_highestCompositionId}；旧结果重放不写入。");
            }

            if (e.DocumentVersion != document.Version)
            {
                return Step.Fail($"结果版本锚点 v{e.DocumentVersion} 与真实文档 v{document.Version} 不一致；过时或未来事件不写入。");
            }

            replacement = e.Replacement;
        }

        if (!TryValidateReplacement(replacement, document, out var rangeError))
        {
            return Step.Fail(rangeError!);
        }

        var result = e.Preedit;
        if (result.Length == 0 && !replacement.IsEmpty)
        {
            // 空/净化后为空的结果不能误删原选区：消费该结果并退休序号，但绝不写成删除。
            RetireResult(e, hasActive);
            if (!hasActive)
            {
                AdmitNoStartIdentity(e);
            }

            var continuedEmpty = ContinueAfterResult(e, document, replacement, after: null, committed: false);
            return Step.Ok(continuedEmpty);
        }

        if (result.Length == 0)
        {
            // 空范围 + 空结果：真实空操作。不调用 setter、不推进版本，事件只消费一次。
            RetireResult(e, hasActive);
            if (!hasActive)
            {
                AdmitNoStartIdentity(e);
            }

            var continuedNoOp = ContinueAfterResult(e, document, replacement, after: null, committed: false);
            return Step.Ok(continuedNoOp);
        }

        EditResult edit;
        try
        {
            edit = commit(replacement, result);
        }
        catch
        {
            // setter/getter 抛错：不伪造成功；按真实回读恢复由调用方负责。事件退休，避免重复写。
            LastResultWriteFailed = true;
            RetireResult(e, hasActive);
            if (!hasActive)
            {
                AdmitNoStartIdentity(e);
            }

            ContinueAfterResult(e, document, replacement, after: null, committed: false);
            return Step.Fail("组合结果写入绑定失败（setter/getter 抛错）；未确认任何提交，事件已消费。");
        }

        RetireResult(e, hasActive);
        if (!hasActive)
        {
            AdmitNoStartIdentity(e);
        }

        if (edit.Status != TextEditStatus.Accepted)
        {
            // 容量整体拒绝 / setter 拒绝 / 净化后无变化：不谎报提交、不推进版本、不删选区，但事件已消费。
            var continued = ContinueAfterResult(e, document, replacement, after: null, committed: false);
            return Step.Ok(continued);
        }

        LastCommit = edit;
        CommitCount++;
        _documentText = edit.After.Text;
        _segmentVersion = edit.After.Version;
        _lastSelection = edit.After.Selection;
        _lastVersion = edit.After.Version;
        updated = document with { Text = edit.After.Text, Version = edit.After.Version, Selection = edit.After.Selection };
        var step = ContinueAfterResult(e, updated, replacement, edit.After, committed: true);
        return Step.Ok(step | TextInputOutcome.ResultCommitted);
    }

    /// <summary>
    /// 结果提交后重新锚定：带下一段预编辑时在<b>相同组合 ID</b> 的下一段继续（按真实回读后的 caret 重新锚定，
    /// 已提交前缀与后缀都保留）；没有下一段时结束当前段，保留身份以便后续 Cancel/End 幂等。
    /// </summary>
    private TextInputOutcome ContinueAfterResult(
        CompositionEvent e,
        CompositionDocument document,
        TextRange replacement,
        TextSnapshot? after,
        bool committed)
    {
        if (e.NextPreedit is null)
        {
            EndSegment(e, document);
            return TextInputOutcome.None;
        }

        if (committed && after is { } snapshot)
        {
            // 真实提交后的下一段：按实际 after 选区/caret 重新锚定（通常是折叠范围），保留前后缀；
            // 预编辑 caret/分段沿用同一原生消息的真实快照，不重猜（R9）。
            var caret = snapshot.Selection.Range;
            _documentText = snapshot.Text;
            _segmentVersion = snapshot.Version;
            _contextIdentity = document.Identity;
            _contextMaxLength = document.MaxLength;
            Active = new CompositionAnchor(
                e.CompositionId,
                snapshot.Version,
                caret,
                snapshot.Selection,
                e.NextPreedit,
                e.NextPreeditCaret ?? e.NextPreedit.Length,
                FreezeSegments(e.NextPreeditSegments));
            IsComposing = true;
            _lastSelection = snapshot.Selection;
            _lastVersion = snapshot.Version;
            return TextInputOutcome.Updated;
        }

        if (Active is null)
        {
            // 没有 Start 的结果 + 下一段预编辑：以事件显式替换区与真实文档建立新段。
            Active = new CompositionAnchor(
                e.CompositionId,
                document.Version,
                replacement,
                document.Selection,
                e.NextPreedit,
                e.NextPreeditCaret ?? e.NextPreedit.Length,
                FreezeSegments(e.NextPreeditSegments));
            IsComposing = true;
            _documentText = document.Text;
            _segmentVersion = document.Version;
            _lastSelection = document.Selection;
            _lastVersion = document.Version;
            _contextIdentity = document.Identity;
            _contextMaxLength = document.MaxLength;
            return TextInputOutcome.Updated;
        }

        // 结果未真实提交（空/无变化/拒绝）：仍在同一未提交段，保留原替换区；
        // 下一段的 caret/分段同样沿用同一原生消息的真实快照（R9）。
        var previous = Active.Value;
        Active = previous with
        {
            Preedit = e.NextPreedit,
            Caret = e.NextPreeditCaret ?? e.NextPreedit.Length,
            Segments = FreezeSegments(e.NextPreeditSegments),
        };
        _documentText = document.Text;
        _lastSelection = document.Selection;
        _lastVersion = document.Version;
        return TextInputOutcome.Updated;
    }

    private void EndSegment(CompositionEvent e, CompositionDocument document)
    {
        IsComposing = false;
        // 返回快照必须反映真实文档当前选区（提交后是真实 caret，事件取消时是运行时当前选区），
        // 不能把组合前锚点当成结果选区。
        _lastSelection = document.Selection;
        Active = null;
        _documentText = document.Text;
        _segmentVersion = document.Version;
        _lastVersion = document.Version;
        _contextIdentity = null;
        _contextMaxLength = int.MaxValue;
        RememberEnded(e.CompositionId);
    }

    private void RetireResult(CompositionEvent e, bool hasActive)
    {
        if (hasActive || e.CompositionId == CompositionId)
        {
            LastSequence = Math.Max(LastSequence, e.Sequence);
        }
    }

    private void AdmitNoStartIdentity(CompositionEvent e)
    {
        _highestCompositionId = e.CompositionId;
        CompositionId = e.CompositionId;
        LastSequence = Math.Max(LastSequence, e.Sequence);
        _contextIdentity = null;
        _contextMaxLength = int.MaxValue;
    }

    // ---------------------------------------------------------------- Cancel / End

    private Step Cancel(CompositionEvent e, CompositionDocument document, out CompositionDocument updated)
    {
        updated = document;

        if (IsComposing && e.CompositionId == CompositionId)
        {
            if (e.Sequence <= LastSequence)
            {
                return Step.Fail($"组合 {e.CompositionId} 的取消序号 {e.Sequence} 未递增（已接受 {LastSequence}）；重复取消不写入。");
            }

            if (e.DocumentVersion != _segmentVersion)
            {
                return Step.Fail($"取消版本锚点 v{e.DocumentVersion} 与活动段版本 v{_segmentVersion} 不一致；过时或未来的取消不结束有效组合。");
            }

            LastSequence = e.Sequence;
            EndSegment(e, document);
            return Step.Ok(TextInputOutcome.Cancelled);
        }

        if (e.CompositionId == _lastEndedCompositionId)
        {
            // 同一已结束组合的重复取消：幂等消费，不影响任何其它活动段。
            return Step.Ok(TextInputOutcome.None);
        }

        return Step.Fail($"组合 ID {e.CompositionId} 不是活动组合也不是最近结束的组合 {_lastEndedCompositionId}；取消不写入。");
    }

    private Step End(CompositionEvent e, CompositionDocument document, out CompositionDocument updated)
    {
        updated = document;

        if (IsComposing && e.CompositionId == CompositionId)
        {
            if (e.Sequence <= LastSequence)
            {
                return Step.Fail($"组合 {e.CompositionId} 的 End 序号 {e.Sequence} 未递增（已接受 {LastSequence}）；重复 End 不写入。");
            }

            if (e.DocumentVersion != _segmentVersion)
            {
                return Step.Fail($"End 版本锚点 v{e.DocumentVersion} 与活动段版本 v{_segmentVersion} 不一致；过时或未来的 End 不结束有效组合。");
            }

            LastSequence = e.Sequence;
            EndSegment(e, document);
            return Step.Ok(TextInputOutcome.Ended);
        }

        if (e.CompositionId == _lastEndedCompositionId)
        {
            // 结果已经结束该段之后的 End：幂等消费。
            return Step.Ok(TextInputOutcome.None);
        }

        return Step.Fail($"组合 ID {e.CompositionId} 不是活动组合也不是最近结束的组合 {_lastEndedCompositionId}；End 不写入。");
    }

    // ---------------------------------------------------------------- 宿主取消

    /// <summary>
    /// 取消未提交段（宿主接缝：焦点/命令/禁用/卸载/窗口失活/鼠标改选区）。
    /// </summary>
    /// <param name="document">当前真实文档视图。</param>
    /// <param name="restoreSelection">
    /// true 时恢复活动段开始前捕获的选区（含方向）；false 时只按新文档夹取（外部文本/来源变化）。
    /// </param>
    /// <param name="restoredSelection">需要恢复的选区；没有活动段时为 null。</param>
    /// <returns>确实取消了未提交段时返回 true；没有组合时为 false（幂等）。</returns>
    public bool Cancel(CompositionDocument document, bool restoreSelection, out TextSelection? restoredSelection)
    {
        restoredSelection = null;
        if (!IsComposing)
        {
            return false;
        }

        var selection = Active!.Value.SelectionBeforeComposition;
        EndSegmentForHost(document);
        if (restoreSelection)
        {
            restoredSelection = selection;
        }

        return true;
    }

    private void EndSegmentForHost(CompositionDocument document)
    {
        var compositionId = CompositionId;
        IsComposing = false;
        Active = null;
        _documentText = document.Text;
        _lastSelection = document.Selection;
        _segmentVersion = document.Version;
        _lastVersion = document.Version;
        _contextIdentity = null;
        _contextMaxLength = int.MaxValue;
        RememberEnded(compositionId);
    }

    /// <summary>记录一次真实文档快照（宿主取消恢复选区后调用，使返回/诊断快照等于真实文档）。</summary>
    internal void RecordDocument(CompositionDocument document) => SyncDocument(document);

    private void SyncDocument(CompositionDocument document)
    {
        _lastReliableDocument = document;
        if (!IsComposing)
        {
            _documentText = document.Text;
            _lastSelection = document.Selection;
            _lastVersion = document.Version;
        }
    }

    private bool MatchesContext(CompositionDocument document)
        => ReferenceEquals(_contextIdentity, document.Identity) && _contextMaxLength == document.MaxLength;

    private void RememberEnded(int compositionId) => _lastEndedCompositionId = compositionId;

    /// <summary>把任意 UTF-16 偏移（相对当前显示串）映射为文档位置（TI-06 消费）。</summary>
    public TextCompositionDisplayMap MapDisplayToDocument(int displayOffset)
    {
        if (Active is not { } anchor)
        {
            return new TextCompositionDisplayMap(TextCompositionDisplayRegion.Committed, displayOffset, 0);
        }

        var prefixLength = anchor.Replacement.Start;
        var preeditLength = anchor.Preedit.Length;
        if (displayOffset < prefixLength)
        {
            return new TextCompositionDisplayMap(TextCompositionDisplayRegion.Committed, displayOffset, 0);
        }

        if (displayOffset < prefixLength + preeditLength)
        {
            return new TextCompositionDisplayMap(TextCompositionDisplayRegion.Preedit, anchor.Replacement.Start, displayOffset - prefixLength);
        }

        return new TextCompositionDisplayMap(
            TextCompositionDisplayRegion.Committed,
            anchor.Replacement.End + (displayOffset - prefixLength - preeditLength),
            0);
    }

    // ---------------------------------------------------------------- 校验

    private static bool TryValidateReplacement(TextRange replacement, CompositionDocument document, out string? error)
    {
        if (replacement.Start < 0 || replacement.End < replacement.Start || replacement.End > document.Length)
        {
            error = $"替换区 {replacement} 超出真实文档 [0, {document.Length}]；不写入。";
            return false;
        }

        if (!TextGraphemeBoundaries.AllAreBoundaries(document.Text, [replacement.Start, replacement.End], out var invalid))
        {
            error = $"替换区 {replacement} 的端点 {invalid} 不在已提交文档的字素边界上；不拆字写入。";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateCommittedText(string text, out string? error)
    {
        if (HasUnpairedSurrogate(text))
        {
            error = "结果文本包含未配对代理项，不能写入文档。";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidatePreeditPayload(string text, int? caret, IReadOnlyList<TextRange>? segments, out string? error)
    {
        if (HasUnpairedSurrogate(text))
        {
            error = "预编辑串包含未配对代理项，不能用于显示或写入。";
            return false;
        }

        if (caret is { } value && (value < 0 || value > text.Length))
        {
            error = $"组合内 caret {value} 超出预编辑串 [0, {text.Length}]。";
            return false;
        }

        if (segments is null)
        {
            error = null;
            return true;
        }

        var previousEnd = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Start < 0 || segment.End < segment.Start || segment.End > text.Length)
            {
                error = $"分段 {segment} 超出预编辑串 [0, {text.Length}]。";
                return false;
            }

            if (segment.Start < previousEnd)
            {
                error = $"分段 {segment} 与前一段重叠或未按顺序排列（前一段结束于 {previousEnd}）。";
                return false;
            }

            previousEnd = segment.End;
        }

        error = null;
        return true;
    }

    private static IReadOnlyList<TextRange> FreezeSegments(IReadOnlyList<TextRange>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return [];
        }

        var copy = new TextRange[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            copy[i] = segments[i];
        }

        return copy;
    }

    private static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>单个事件的应用结论：被拒绝（无副作用）或已消费（含真实写入动作）。</summary>
    private readonly record struct Step(bool Rejected, TextInputOutcome Outcome, string? Reason)
    {
        public static Step Ok(TextInputOutcome outcome) => new(false, outcome, null);

        public static Step Fail(string reason) => new(true, TextInputOutcome.Rejected, reason);
    }

    /// <summary>
    /// 未提交段锚点：替换区（绝对文档坐标）、组合前选区、预编辑、caret 与分段都在本段开始或真实结果重基后冻结；
    /// Update 只改预编辑/caret/分段，不按最新 caret 重新猜测替换区。
    /// </summary>
    internal readonly record struct CompositionAnchor(
        int CompositionId,
        int DocumentVersion,
        TextRange Replacement,
        TextSelection SelectionBeforeComposition,
        string Preedit,
        int Caret,
        IReadOnlyList<TextRange> Segments);
}

/// <summary>提交一段组合结果所需的真实文档视图（由 TextField 客户端注入，全部来自真实回读）。</summary>
/// <param name="Text">真实已提交文本。</param>
/// <param name="Version">真实文档版本。</param>
/// <param name="Selection">真实运行时选区。</param>
/// <param name="Identity">当前绑定来源身份（用于识别换源）。</param>
/// <param name="MaxLength">当前生效的 MaxLength。</param>
internal readonly record struct CompositionDocument(
    string Text,
    int Version,
    TextSelection Selection,
    object Identity,
    int MaxLength)
{
    /// <summary>UTF-16 长度。</summary>
    public int Length => Text.Length;

    /// <summary>当前真实文档快照。</summary>
    public TextSnapshot Snapshot => new(Text, Selection, Version);
}

/// <summary>把一段组合结果作为一次真实 TI-03 事务提交的委托；返回真实事务结果。</summary>
/// <param name="range">被替换的已提交范围。</param>
/// <param name="result">接受的已提交文本。</param>
/// <returns>真实事务结果（回读确认后的版本、文本与选区）。</returns>
internal delegate EditResult CompositionCommit(TextRange range, string result);

/// <summary>显示串位置属于已提交文档还是未提交预编辑。</summary>
internal enum TextCompositionDisplayRegion
{
    /// <summary>偏移落在已提交文本上。</summary>
    Committed,

    /// <summary>偏移落在未提交预编辑上。</summary>
    Preedit,
}

/// <summary>
/// 显示串与文档位置的映射（TI-06 消费）：显示串由 <c>前缀 + 预编辑 + 后缀</c> 组成，
/// 预编辑整体替换活动段的替换区，因此任何位置都能还原到已提交文档坐标。
/// </summary>
internal readonly record struct TextCompositionDisplayMap(
    TextCompositionDisplayRegion Region,
    int DocumentOffset,
    int PreeditOffset)
{
    /// <summary>已提交文本偏移；偏移落在预编辑上时返回替换区起点。</summary>
    public int ToDocumentOffset() => DocumentOffset;

    /// <summary>显示串中的预编辑偏移；偏移落在已提交文本上时返回 0。</summary>
    public int ToPreeditOffset() => PreeditOffset;

    /// <summary>偏移是否落在未提交预编辑上。</summary>
    public bool IsPreedit => Region == TextCompositionDisplayRegion.Preedit;

    public override string ToString() => $"{Region} doc={DocumentOffset} preedit={PreeditOffset}";
}
