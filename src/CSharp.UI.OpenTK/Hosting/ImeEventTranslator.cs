namespace CSharp.UI.OpenTK;

/// <summary>
/// 把 IMM32 消息快照翻译成组合事件批次：负责组合 ID/序号/版本锚点/替换区的来源归属。
/// </summary>
/// <remarks>
/// <para>
/// 身份三元组（会话 epoch、组合 ID、跨消息序号）与文档版本锚点全部来自真实回读：
/// 序号是会话内单调递增的计数器（跨段不重置）；组合 ID 在每次 Start 与每个“无 Start 的新段”上递增；
/// 段版本锚点取段开始时的真实文档版本，每次已确认结果之后按 <see cref="TextInputApplyResult.Document"/>
/// 的真实版本重锚，绝不按文本相等或时间窗口推断。
/// </para>
/// <para>
/// 结果判定沿用 TI-01 已审核规则：只有本条消息的 <c>GCS_RESULTSTR</c> 标志 + 本条查询到的非空结果串
/// 才产生 Result 事件；无标志但查询有数据只作快照，绝不重复提交。
/// </para>
/// </remarks>
internal sealed class ImeEventTranslator
{
    private int _sessionId;
    private int _nextCompositionId;
    private int _sequence;
    private int _currentCompositionId = -1;
    private int _segmentVersion;
    private TextRange _segmentReplacement;

    /// <summary>绑定新的焦点会话；组合 ID 重新计数（epoch 已隔离，不会与旧会话混淆）。</summary>
    internal void BeginSession(int sessionId)
    {
        _sessionId = sessionId;
        _nextCompositionId = 0;
        _sequence = 0;
        _currentCompositionId = -1;
        _segmentVersion = 0;
        _segmentReplacement = default;
    }

    /// <summary>会话结束：清空组合跟踪状态。</summary>
    internal void EndSession()
    {
        _currentCompositionId = -1;
        _segmentVersion = 0;
        _segmentReplacement = default;
    }

    /// <summary>当前原生组合 ID；没有组合时为 -1。</summary>
    internal int CurrentCompositionId => _currentCompositionId;

    /// <summary>上一次应用批次的真实文档版本（用于下一段锚定）。</summary>
    internal int SegmentVersion => _segmentVersion;

    /// <summary>
    /// WM_IME_STARTCOMPOSITION：开始一个新段。客户端仍在组合（旧的未提交段残留）时先取消再开始。
    /// </summary>
    internal List<CompositionEvent> OnStartComposition(ITextInputClient client)
    {
        var events = new List<CompositionEvent>(2);
        if (client.IsComposing && _currentCompositionId >= 0)
        {
            // 旧段残留：先结束它（取消未提交内容），避免新 Start 被状态机拒绝。
            events.Add(CompositionEvent.Cancel(_sessionId, _currentCompositionId, _segmentVersion, NextSequence()));
        }

        var snapshot = client.Document;
        _currentCompositionId = _nextCompositionId++;
        _segmentVersion = snapshot.Version;
        _segmentReplacement = snapshot.Selection.Range;
        events.Add(CompositionEvent.Start(
            _sessionId,
            _currentCompositionId,
            snapshot.Version,
            NextSequence(),
            snapshot.Selection.Range,
            preedit: null,
            selectionBeforeComposition: snapshot.Selection));
        return events;
    }

    /// <summary>
    /// WM_IME_COMPOSITION：按本条消息的标志与当场读数产出 Result / Start / Update 事件批次。
    /// </summary>
    /// <param name="client">当前绑定的文本输入客户端。</param>
    /// <param name="data">当场复制的组合快照（字符串已按 UTF-16 解码）。</param>
    internal List<CompositionEvent> OnComposition(ITextInputClient client, ImmCompositionSnapshot data)
    {
        var events = new List<CompositionEvent>(2);
        var result = data.ResultString;
        var hasNewResult = data.HasResultFlag && !string.IsNullOrEmpty(result);
        var compStr = data.CompositionString;

        if (hasNewResult)
        {
            // 结果版本锚点：有活动段用段版本，否则（平台真实存在的“无 Start 结果”）锚定当前真实文档版本，
            // 并使用新组合 ID（状态机要求超过已见水位）。
            var composing = client.IsComposing;
            var compositionId = composing ? _currentCompositionId : _nextCompositionId++;
            var version = composing ? _segmentVersion : client.Document.Version;
            var replacement = composing ? _segmentReplacement : client.Document.Selection.Range;
            var nextPreedit = data.HasPreeditData && !string.IsNullOrEmpty(compStr) ? compStr : null;
            // 结果+下一段同属本条消息：下一段的原生 caret 与分段照抄同一快照，不靠主机猜测（R9）。
            var nextCaret = nextPreedit is { } np && data.CursorPosition >= 0
                ? Math.Clamp(data.CursorPosition, 0, np.Length)
                : (int?)null;
            var nextSegments = nextPreedit is { } np2 ? BuildSegments(data.ClauseOffsets, np2.Length) : null;
            events.Add(CompositionEvent.ResultWithNextPreeditAttributes(
                _sessionId, compositionId, version, NextSequence(), result, replacement,
                nextPreedit, nextCaret, nextSegments));

            if (!composing)
            {
                // 无 Start 结果已被身份接纳：把它登记为当前组合，随后的 Cancel/End 走幂等路径。
                _currentCompositionId = compositionId;
                _segmentVersion = version;
                _segmentReplacement = replacement;
            }

            // 结果+下一段预编辑由 Result.NextPreedit 一并表达（状态机先结果后预编辑重锚），
            // 这里不再重复发 Update。
            return events;
        }

        if ((data.Flags & Win32Ime.GcsAnyMask) == 0)
        {
            // WM_IME_COMPOSITION 的 lParam 完全不含 GCS_* 标志才表示平台取消当前组合
            // （MSDN：lParam 为 0 时组合被取消，自绘应用应丢弃预编辑）；
            // 仅带 RESULTCLAUSE 等结果元数据的消息仍是数据更新，不是取消（R6）。
            // 按活动段身份发 Cancel，不依赖随后必定出现的 WM_IME_ENDCOMPOSITION 才能清理。
            if (client.IsComposing && _currentCompositionId >= 0)
            {
                events.Add(CompositionEvent.Cancel(
                    _sessionId, _currentCompositionId, _segmentVersion, NextSequence()));
            }

            return events;
        }

        if (!data.HasPreeditData)
        {
            // 只有结果侧元数据（RESULTREAD*/RESULTCLAUSE 等）而没有预编辑数据：不是取消也不是新提交，
            // 本条不产生任何事件（R6）。
            return events;
        }

        var preedit = compStr ?? string.Empty;
        var caret = data.CursorPosition >= 0 ? Math.Clamp(data.CursorPosition, 0, preedit.Length) : (int?)null;
        var segments = BuildSegments(data.ClauseOffsets, preedit.Length);

        if (client.IsComposing)
        {
            events.Add(CompositionEvent.Update(
                _sessionId, _currentCompositionId, _segmentVersion, NextSequence(),
                _segmentReplacement, preedit, caret, segments));
        }
        else if (preedit.Length > 0)
        {
            // 没有 WM_IME_STARTCOMPOSITION 的隐式新段（例如分段提交后继续输入）：
            // 用新组合 ID 以当前文档版本与选区锚定。空预编辑不建立新段。
            var snapshot = client.Document;
            _currentCompositionId = _nextCompositionId++;
            _segmentVersion = snapshot.Version;
            _segmentReplacement = snapshot.Selection.Range;
            events.Add(CompositionEvent.Start(
                _sessionId,
                _currentCompositionId,
                snapshot.Version,
                NextSequence(),
                snapshot.Selection.Range,
                preedit,
                selectionBeforeComposition: snapshot.Selection,
                compositionCaret: caret,
                segments: segments));
        }

        return events;
    }

    /// <summary>
    /// WM_IME_ENDCOMPOSITION：仍有未提交段时先取消（恢复段开始前选区），再发 End（幂等）。
    /// </summary>
    internal List<CompositionEvent> OnEndComposition(ITextInputClient client)
    {
        var events = new List<CompositionEvent>(2);
        if (_currentCompositionId < 0)
        {
            return events;
        }

        if (client.IsComposing)
        {
            events.Add(CompositionEvent.Cancel(_sessionId, _currentCompositionId, _segmentVersion, NextSequence()));
        }

        events.Add(CompositionEvent.End(_sessionId, _currentCompositionId, _segmentVersion, NextSequence()));
        return events;
    }

    /// <summary>
    /// 批次应用后按真实回读结果重锚段版本与替换区（只有应用后的真实文档才可作为下一段锚点）。
    /// </summary>
    internal void NoteApplied(TextInputApplyResult result)
    {
        _segmentVersion = result.Document.Version;
        _segmentReplacement = result.Document.Selection.Range;
    }

    /// <summary>把 GCS_COMPCLAUSE 的 UTF-16 偏移数组换算为顺序分段；非法数据降级为空分段（不拒绝整条事件）。</summary>
    private static IReadOnlyList<TextRange> BuildSegments(IReadOnlyList<int> clauses, int preeditLength)
    {
        if (clauses.Count < 2 || clauses[0] != 0 || clauses[^1] > preeditLength)
        {
            return [];
        }

        var segments = new List<TextRange>(clauses.Count - 1);
        for (var i = 0; i + 1 < clauses.Count; i++)
        {
            var start = clauses[i];
            var end = clauses[i + 1];
            if (start < 0 || end <= start || end > preeditLength)
            {
                return [];
            }

            segments.Add(new TextRange(start, end));
        }

        return segments;
    }

    private int NextSequence() => _sequence++;
}
