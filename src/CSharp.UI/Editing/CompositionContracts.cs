using System.Collections.Immutable;

namespace CSharp.UI;

/// <summary>组合事件种类：一段组合由这些阶段组成，结果与取消互斥。</summary>
/// <remarks>
/// 顺序契约（由 TI-04 的组合状态机实际执行，本任务只冻结事件形状与文档）：
/// <c>Start → Update* → (Result | Cancel) → End</c>；一段组合可跨多条原生消息，
/// 因此 Update/Result/Cancel/End 的批次不要求再带 Start。真实平台也存在“没有 Start 的 Result”序列，
/// 消费方必须允许它，并以 Result 的替换区与版本锚定为准。
/// </remarks>
public enum CompositionEventKind
{
    /// <summary>开始一段组合。</summary>
    Start,

    /// <summary>更新预编辑（不写入绑定）。</summary>
    Update,

    /// <summary>提交当前预编辑为已提交文本。</summary>
    Result,

    /// <summary>取消当前未提交的预编辑。</summary>
    Cancel,

    /// <summary>结束组合（之后不应再发送该组合 ID 的事件）。</summary>
    End,
}

/// <summary>
/// 一段组合事件：携带焦点会话 epoch、组合 ID、序号、文档版本锚点、替换区、预编辑串与分段属性。
/// </summary>
/// <remarks>
/// <para>
/// <b>与 <see cref="TextInputEvent"/> 的区别：</b>组合事件描述“尚未提交/正在提交”的输入过程，
/// <see cref="TextInputEvent"/> 继续只表示已经上屏的文本；两者不互相换义，旧代码路径不变。
/// </para>
/// <para>
/// <b>会话身份：</b><see cref="CompositionSessionId"/> 来自焦点会话 epoch（<see cref="TextInputSession.SessionId"/>），
/// <see cref="CompositionId"/> 在同一 epoch 内唯一，<see cref="Sequence"/> 是<b>同一组合跨消息</b>递增的序号
/// （一段组合通常分属多条原生消息，序号不随消息重置）。三者共同构成身份三元组；
/// 消费方必须据此隔离旧事件，不得靠字符串或时间窗口去重。
/// </para>
/// <para>
/// <b>版本锚点：</b><see cref="DocumentVersion"/> 是事件锚定的文档版本，必须等于应用该事件时的文档版本
/// （<see cref="IsAnchoredTo"/>）；不存在的未来版本与过时版本都不得写入。同一原生消息中“结果 + 下一段预编辑”
/// 的重基由 TI-04 依据真实事务结果完成（只有事务确实提交、版本确实推进时才推进锚点）。
/// </para>
/// <para>
/// <b>顺序与过时抑制由 TI-04 实际执行：</b>TI-02 不提供跨消息的状态机、游标或判定报告；
/// 序号递增、重复/过时/乱序拒绝、以及“提交结果后重新锚定”都必须在 TI-04 中与真实编辑事务
/// （含 setter 拒绝、净化后无变化、MaxLength 全拒绝等真实结果）共同实现。
/// </para>
/// <para>
/// <b>不可变性：</b><see cref="Segments"/> 只以只读列表暴露且内部为不可变集合，取出后无法改写事件内容；
/// 标量属性在 <c>init</c> 后不可变。
/// </para>
/// <para>
/// <b>default 语义：</b><c>default(CompositionEvent)</c> 等价于一个空 <c>Start(0)</c>
/// （会话/组合/序号/版本为 0、替换区 <c>[0,0)</c>、空预编辑），其形状是<b>合法</b>的空载荷；
/// 是否可应用仍取决于会话、版本锚点与顺序（由 TI-04 判定）。所有查询对 default 安全。
/// </para>
/// <para><b>诊断不含用户原文：</b><see cref="ToString"/> 只输出版本/长度等元数据。</para>
/// </remarks>
public readonly record struct CompositionEvent
{
    private readonly string? _preedit;
    private readonly ImmutableArray<TextRange> _segments;

    private CompositionEvent(int sessionId, int compositionId, int sequence, int documentVersion, CompositionEventKind kind)
    {
        CompositionSessionId = sessionId;
        CompositionId = compositionId;
        Sequence = sequence;
        DocumentVersion = documentVersion;
        Kind = kind;
    }

    /// <summary>开始组合。</summary>
    /// <param name="sessionId">组合会话 ID（焦点 epoch 内唯一，必须 &gt;= 0）。</param>
    /// <param name="compositionId">组合 ID（同一会话内唯一，必须 &gt;= 0）。</param>
    /// <param name="documentVersion">事件锚定的文档版本（必须 &gt;= 0）。</param>
    /// <param name="sequence">序号（同一组合内从 0 起递增）。</param>
    /// <param name="replacement">被预编辑替代的已提交范围。</param>
    /// <param name="preedit">预编辑串；null 视为空串。</param>
    /// <param name="selectionBeforeComposition">组合开始前的选区快照；取消时恢复它。</param>
    /// <param name="compositionCaret">组合内 caret 的 UTF-16 偏移（相对预编辑串，可为 null 表示由主机推断）。</param>
    /// <param name="segments">分段属性（预编辑串上的 UTF-16 区间）；会被复制为不可变集合。</param>
    /// <exception cref="ArgumentOutOfRangeException">ID/版本/序号/组合内 caret 为负。</exception>
    public static CompositionEvent Start(
        int sessionId,
        int compositionId,
        int documentVersion,
        int sequence,
        TextRange replacement,
        string? preedit = null,
        TextSelection? selectionBeforeComposition = null,
        int? compositionCaret = null,
        IReadOnlyList<TextRange>? segments = null)
    {
        ValidateIdentity(sessionId, compositionId, sequence, documentVersion);
        ValidateCompositionCaret(compositionCaret);
        return new CompositionEvent(sessionId, compositionId, sequence, documentVersion, CompositionEventKind.Start)
        {
            Replacement = replacement,
            Preedit = preedit,
            SelectionBeforeComposition = selectionBeforeComposition,
            CompositionCaret = compositionCaret,
            Segments = segments,
        };
    }

    /// <summary>更新预编辑。</summary>
    public static CompositionEvent Update(
        int sessionId,
        int compositionId,
        int documentVersion,
        int sequence,
        TextRange replacement,
        string? preedit = null,
        int? compositionCaret = null,
        IReadOnlyList<TextRange>? segments = null)
    {
        ValidateIdentity(sessionId, compositionId, sequence, documentVersion);
        ValidateCompositionCaret(compositionCaret);
        return new CompositionEvent(sessionId, compositionId, sequence, documentVersion, CompositionEventKind.Update)
        {
            Replacement = replacement,
            Preedit = preedit,
            CompositionCaret = compositionCaret,
            Segments = segments,
        };
    }

    /// <summary>提交当前段结果（一次整段原子替换，不是逐字符历史）。</summary>
    /// <param name="result">本段接受的已提交文本。</param>
    /// <param name="replacement">被结果替换的范围。</param>
    /// <param name="nextPreedit">
    /// 同一原生消息里紧跟结果之后的下一段预编辑；消费方必须按“先结果、后预编辑”处理，
    /// 并以结果确认后的真实文档版本重新锚定（TI-04）。
    /// </param>
    public static CompositionEvent Result(
        int sessionId,
        int compositionId,
        int documentVersion,
        int sequence,
        string? result,
        TextRange replacement = default,
        string? nextPreedit = null)
    {
        ValidateIdentity(sessionId, compositionId, sequence, documentVersion);
        return new CompositionEvent(sessionId, compositionId, sequence, documentVersion, CompositionEventKind.Result)
        {
            Replacement = replacement,
            Preedit = result,
            NextPreedit = nextPreedit,
        };
    }

    /// <summary>
    /// <see cref="Result"/> 的平台桥入口：附带下一段预编辑的原生 caret/分段（同一消息的真实快照）。
    /// internal：不扩大公开契约面；caret/分段相对 <paramref name="nextPreedit"/> 解释。
    /// </summary>
    internal static CompositionEvent ResultWithNextPreeditAttributes(
        int sessionId,
        int compositionId,
        int documentVersion,
        int sequence,
        string? result,
        TextRange replacement,
        string? nextPreedit,
        int? nextPreeditCaret,
        IReadOnlyList<TextRange>? nextPreeditSegments)
    {
        var e = Result(sessionId, compositionId, documentVersion, sequence, result, replacement, nextPreedit);
        ValidateCompositionCaret(nextPreeditCaret);
        return e with
        {
            NextPreeditCaret = nextPreeditCaret,
            NextPreeditSegments = nextPreeditSegments is { } list ? Freeze(list) : null,
        };
    }

    /// <summary>取消当前未提交的组合段。</summary>
    public static CompositionEvent Cancel(int sessionId, int compositionId, int documentVersion, int sequence = 0)
    {
        ValidateIdentity(sessionId, compositionId, sequence, documentVersion);
        return new CompositionEvent(sessionId, compositionId, sequence, documentVersion, CompositionEventKind.Cancel);
    }

    /// <summary>结束组合（重复结束必须幂等，由 TI-04 的状态机处理）。</summary>
    public static CompositionEvent End(int sessionId, int compositionId, int documentVersion, int sequence = 0)
    {
        ValidateIdentity(sessionId, compositionId, sequence, documentVersion);
        return new CompositionEvent(sessionId, compositionId, sequence, documentVersion, CompositionEventKind.End);
    }

    /// <summary>组合会话 ID（来自焦点 epoch）。</summary>
    public int CompositionSessionId { get; }

    /// <summary>组合 ID（会话内唯一）。</summary>
    public int CompositionId { get; }

    /// <summary>同一组合<b>跨消息</b>递增的序号（不随消息批次重置）。</summary>
    public int Sequence { get; }

    /// <summary>事件锚定的文档版本。</summary>
    public int DocumentVersion { get; }

    /// <summary>
    /// 事件种类；未定义值会被 <see cref="TryValidatePayload(TextSnapshot, out string?)"/> 拒绝
    /// （<c>default</c>/<c>with</c> 构造不能绕过有效性检查）。
    /// </summary>
    public CompositionEventKind Kind { get; init; }

    /// <summary>被预编辑替代/结果替换的已提交范围。</summary>
    public TextRange Replacement { get; init; }

    /// <summary>预编辑串（Start/Update）或本段结果（Result）；default 事件返回空串而不是 null。</summary>
    public string Preedit
    {
        get => _preedit ?? string.Empty;
        init => _preedit = value;
    }

    /// <summary>组合开始前的选区快照；仅 Start 有意义，取消时恢复它。</summary>
    public TextSelection? SelectionBeforeComposition { get; init; }

    /// <summary>组合内 caret 的 UTF-16 偏移（相对 <see cref="Preedit"/>）；null 表示由主机按排版规则处理。</summary>
    public int? CompositionCaret { get; init; }

    /// <summary>分段属性：预编辑串上的一段区间（例如活动段）；只读且不可变，空表示无分段信息。</summary>
    public IReadOnlyList<TextRange> Segments
    {
        get => _segments.IsDefaultOrEmpty ? [] : _segments;
        init => _segments = Freeze(value);
    }

    /// <summary>结果事件携带的下一段预编辑；不为 null 时按“先结果、后预编辑”处理。</summary>
    public string? NextPreedit { get; init; }

    /// <summary>
    /// 下一段预编辑的原生 caret（相对 <see cref="NextPreedit"/> 的 UTF-16 偏移）；
    /// 仅随 <see cref="Result"/> 出厂设置，为空时消费方按自身排版规则（默认末尾）处理。
    /// internal：平台桥与状态机共享，不改变公开契约面。
    /// </summary>
    internal int? NextPreeditCaret { get; init; }

    /// <summary>
    /// 下一段预编辑的原生分段属性（相对 <see cref="NextPreedit"/> 的 UTF-16 区间）；
    /// 与 <see cref="NextPreeditCaret"/> 同样仅由 Result 工厂填充，由 TI-04 在重锚后生效。
    /// </summary>
    internal IReadOnlyList<TextRange>? NextPreeditSegments { get; init; }

    /// <summary>该事件是否请求产生一次原子提交事务（是否真的提交由 TI-04 依据真实事务结果判定）。</summary>
    public bool CommitsResult => Kind == CompositionEventKind.Result;

    /// <summary>该事件是否结束当前段（Result/End/Cancel 都结束当前段）。</summary>
    public bool EndsComposition => Kind is CompositionEventKind.Result or CompositionEventKind.Cancel or CompositionEventKind.End;

    /// <summary>该事件的版本锚点是否与给定快照一致（未来的重基由 TI-04 在真实事务提交后完成）。</summary>
    public bool IsAnchoredTo(TextSnapshot document) => DocumentVersion == document.Version;

    /// <summary>只输出会话/组合/序号/版本/种类与预编辑长度，不输出用户原文。</summary>
    public override string ToString()
        => $"{Kind} session={CompositionSessionId} composition={CompositionId} seq={Sequence} v{DocumentVersion}"
            + (Kind is CompositionEventKind.Start or CompositionEventKind.Update or CompositionEventKind.Result
                ? $" preeditLen={Preedit.Length}"
                : string.Empty);

    /// <summary>
    /// 校验事件是否锚定到给定文档并具有可安全应用的负载（形状、范围与字素边界）。
    /// </summary>
    /// <param name="document">应用该事件时的文档快照。</param>
    /// <param name="error">失败原因；成功时为 null。</param>
    /// <remarks>
    /// <para>
    /// 这是<b>完整</b>重载：除长度/形状规则外，还校验 <see cref="Replacement"/> 与
    /// <see cref="SelectionBeforeComposition"/> 的端点是否落在<b>已提交文档的字素边界</b>上
    /// （使用核心 <see cref="System.Globalization.StringInfo"/>，不依赖字体或渲染器），
    /// 因此不会把代理对、组合字符或 ZWJ 序列从中间拆开。
    /// </para>
    /// <para>
    /// 它只回答“形状/锚点/字素边界是否可应用”，不回答“序号是否递增、是否重复、是否过时”；
    /// 后者必须由 TI-04 的组合状态机与真实编辑事务共同判定。
    /// </para>
    /// </remarks>
    public bool TryValidatePayload(TextSnapshot document, out string? error)
    {
        if (!IsAnchoredTo(document))
        {
            error = $"事件版本锚点 v{DocumentVersion} 与文档版本 v{document.Version} 不一致；" +
                "过时事件与尚不存在的未来版本都不得写入。";
            return false;
        }

        if (!TryValidatePayload(document.Length, out error))
        {
            return false;
        }

        // 完整重载额外要求：替换区与组合前选区端点在文档字素边界上。
        if (!TextGraphemeBoundaries.AllAreBoundaries(document.Text, [Replacement.Start, Replacement.End], out var invalid))
        {
            error = $"替换区 {Replacement} 的端点 {invalid} 不在已提交文档的字素边界上；" +
                "从字素内部替换会把代理对/组合字符拆开。";
            return false;
        }

        if (SelectionBeforeComposition is { } selection
            && !TextGraphemeBoundaries.AllAreBoundaries(document.Text, [selection.Anchor, selection.Active], out var invalidSelection))
        {
            error = $"组合前选区 {selection} 的端点 {invalidSelection} 不在已提交文档的字素边界上。";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 仅按长度校验事件负载是否可安全应用（<b>较弱</b>重载，不检查字素边界，也不检查版本锚点）。
    /// </summary>
    /// <param name="documentLength">已提交文档的 UTF-16 长度。</param>
    /// <param name="error">失败原因；成功时为 null。</param>
    /// <remarks>
    /// <para>
    /// <b>该重载不能保证字素安全：</b>只按长度判断范围/选区是否越界，因此像 <c>[1,1)</c> 这样
    /// “长度合法但位于代理对内部”的范围会通过。需要字素安全的消费方必须使用
    /// <see cref="TryValidatePayload(TextSnapshot, out string?)"/>。
    /// </para>
    /// <para>
    /// 规则：事件种类必须是已定义枚举值；替换区与组合前选区必须落在文档长度内；预编辑串不得包含
    /// 未配对代理项；组合内 caret 与分段必须落在预编辑串内、按顺序且不重叠；<see cref="NextPreedit"/>
    /// 与预编辑同样不得拆代理项。
    /// </para>
    /// <para>
    /// 组合内 <see cref="CompositionCaret"/> 与 <see cref="Segments"/> 是相对<b>预编辑串</b>的 UTF-16 内部位置，
    /// 可能与文档字素边界不同；到排版 caret 的映射由后续排版阶段（TI-06）负责，两者坐标不混用。
    /// </para>
    /// <para>
    /// 调用者参数错误（如负的 <paramref name="documentLength"/>）抛
    /// <see cref="ArgumentOutOfRangeException"/>；只有来自平台的数据形状问题才返回 false。
    /// </para>
    /// </remarks>
    public bool TryValidatePayload(int documentLength, out string? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentLength);

        if (!Enum.IsDefined(Kind))
        {
            error = $"组合事件种类 {(int)Kind} 未定义；未知事件类型不得写入。";
            return false;
        }

        if (Replacement.Start < 0 || Replacement.End < Replacement.Start || Replacement.End > documentLength)
        {
            error = $"替换区 {Replacement} 超出文档 [0, {documentLength}]。";
            return false;
        }

        if (SelectionBeforeComposition is { } selection
            && (selection.Anchor < 0 || selection.Anchor > documentLength
                || selection.Active < 0 || selection.Active > documentLength))
        {
            error = $"组合前选区 {selection} 超出文档 [0, {documentLength}]。";
            return false;
        }

        var text = Preedit;
        if (HasUnpairedSurrogate(text))
        {
            error = "预编辑串包含未配对代理项，不能拆开写入文本。";
            return false;
        }

        if (NextPreedit is { } next && HasUnpairedSurrogate(next))
        {
            error = "下一段预编辑串包含未配对代理项，不能拆开写入文本。";
            return false;
        }

        // 下一段预编辑的原生属性（TI-07 结果+下一段同消息）：caret 与分段都必须落在下一段串内。
        if (NextPreeditCaret is { } nextCaret
            && (nextCaret < 0 || nextCaret > (NextPreedit?.Length ?? 0)))
        {
            error = $"下一段预编辑 caret {nextCaret} 超出下一段串 [0, {NextPreedit?.Length ?? 0}]。";
            return false;
        }

        if (NextPreeditSegments is { } nextSegments && nextSegments.Count > 0)
        {
            var nextLength = NextPreedit?.Length ?? 0;
            var nextPreviousEnd = 0;
            for (var i = 0; i < nextSegments.Count; i++)
            {
                var segment = nextSegments[i];
                if (segment.Start < 0 || segment.End < segment.Start || segment.End > nextLength)
                {
                    error = $"下一段预编辑分段 {segment} 超出下一段串 [0, {nextLength}]。";
                    return false;
                }

                if (segment.Start < nextPreviousEnd)
                {
                    error = $"下一段预编辑分段 {segment} 与前一段重叠或未按顺序排列（前一段结束于 {nextPreviousEnd}）。";
                    return false;
                }

                nextPreviousEnd = segment.End;
            }
        }

        if (CompositionCaret is { } caret && (caret < 0 || caret > text.Length))
        {
            error = $"组合内 caret {caret} 超出预编辑串 [0, {text.Length}]。";
            return false;
        }

        var segments = Segments;
        if (segments.Count > 0)
        {
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
        }

        error = null;
        return true;
    }

    private static void ValidateIdentity(int sessionId, int compositionId, int sequence, int documentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(compositionId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(documentVersion);
    }

    private static void ValidateCompositionCaret(int? compositionCaret)
    {
        if (compositionCaret is { } caret)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(caret);
        }
    }

    /// <summary>把任意输入复制为不可变分段集合；null/空/default 都归一化为空集合。</summary>
    private static ImmutableArray<TextRange> Freeze(IReadOnlyList<TextRange>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return [];
        }

        if (segments is ImmutableArray<TextRange> immutable)
        {
            return immutable.IsDefault ? [] : immutable;
        }

        var builder = ImmutableArray.CreateBuilder<TextRange>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            builder.Add(segments[i]);
        }

        return builder.MoveToImmutable();
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
}
