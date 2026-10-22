using System.Collections.Immutable;

namespace CSharp.UI.OpenTK;
/// <summary>
/// 已提交文档的只读权威快照 + 显示镜像（TI-07 P2a）：预编辑<b>整体替换</b>替换区，不是追加。
/// </summary>
/// <remarks>
/// <para>
/// <b>唯一权威：</b><see cref="Committed"/> 来自真实回读（客户端 <see cref="ITextInputClient.Document"/>），
/// 预编辑只存在于 <see cref="Preedit"/> 里，永远不写绑定、不进入文档版本、不进入历史。
/// </para>
/// <para>
/// <b>显示镜像：</b><see cref="DisplayText"/> = <c>前缀 + 预编辑 + 后缀</c>，其中前缀是
/// <c>[0, Replacement.Start)</c>、后缀是 <c>[Replacement.End, len)</c>。因此有前缀/后缀时也能正确显示，
/// 而不是把预编辑拼到末尾。
/// </para>
/// <para>
/// <b>三套坐标严格区分：</b>已提交文档偏移、显示镜像偏移、原生（平台）偏移。原生偏移在 TSF 里通常是
/// “display attribute / ACP”坐标，与已提交文档偏移<b>不相等</b>；本类型的映射方法全部显式命名，
/// 不做隐式换算。
/// </para>
/// <para><b>不可变：</b>构造后所有属性只读，<see cref="Segments"/> 是冻结副本。</para>
/// <para><b>诊断不含用户原文：</b><see cref="ToString"/> 只输出长度/版本/范围。</para>
/// </remarks>
internal sealed class TextInputAuthoritativeSnapshot
{
    private readonly ImmutableArray<TextRange> _segments;

    private TextInputAuthoritativeSnapshot(
        TextSnapshot committed,
        TextRange replacement,
        string preedit,
        int preeditCaret,
        ImmutableArray<TextRange> segments,
        long mirrorRevision,
        long nativeRevision,
        bool composing)
    {
        Committed = committed;
        Replacement = replacement;
        _preedit = preedit;
        PreeditCaret = preeditCaret;
        _segments = segments;
        MirrorRevision = mirrorRevision;
        NativeRevision = nativeRevision;
        IsComposing = composing;
    }

    private readonly string _preedit;

    /// <summary>已提交文档的真实权威快照（文本、选区、版本）。</summary>
    public TextSnapshot Committed { get; }

    /// <summary>预编辑在已提交文档中的替换区（已提交坐标）。</summary>
    public TextRange Replacement { get; }

    /// <summary>未提交预编辑串；无组合时为空串。</summary>
    public string Preedit => _preedit;

    /// <summary>预编辑 caret（相对预编辑串的 UTF-16 偏移）。</summary>
    public int PreeditCaret { get; }

    /// <summary>预编辑分段（相对预编辑串的 UTF-16 区间）。</summary>
    /// <remarks>
    /// 真正的不可变集合（<see cref="ImmutableArray{T}"/>）：消费者无法向下转型为可写数组去修改已发布快照，
    /// 旧快照在后续更新后也保持稳定。
    /// </remarks>
    public IReadOnlyList<TextRange> Segments => _segments.IsDefaultOrEmpty ? [] : _segments;

    /// <summary>
    /// 显示/镜像修订号：预编辑、替换区、显示串或显示 caret 每次真实变化都单调递增。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Committed"/> 的 <see cref="TextSnapshot.Version"/> <b>不同</b>：镜像可以在文档版本不变时
    /// 变化很多次（每个预编辑按键一次），反过来提交后文档版本推进而镜像修订也推进。两者不得互相代替。
    /// </remarks>
    public long MirrorRevision { get; }

    /// <summary>
    /// 原生（平台）修订号：由平台侧可见状态变化推进，供 TSF 判断“我看到的镜像是否还是最新的”。
    /// </summary>
    /// <remarks>与 <see cref="MirrorRevision"/> 分开：平台可能只改原生选区而不改镜像文本，反之亦然。</remarks>
    public long NativeRevision { get; }

    /// <summary>当前是否处于未提交的组合状态。</summary>
    public bool IsComposing { get; }

    /// <summary>显示镜像串：<c>已提交前缀 + 预编辑 + 已提交后缀</c>。</summary>
    public string DisplayText
    {
        get
        {
            var text = Committed.Text;
            var start = Math.Clamp(Replacement.Start, 0, text.Length);
            var end = Math.Clamp(Replacement.End, 0, text.Length);
            if (end < start)
            {
                end = start;
            }

            return string.Concat(text.AsSpan(0, start), _preedit, text.AsSpan(end));
        }
    }

    /// <summary>显示镜像串的 UTF-16 长度。</summary>
    public int DisplayLength => DisplayText.Length;

    /// <summary>已提交文档的 UTF-16 长度。</summary>
    public int CommittedLength => Committed.Length;

    /// <summary>显示镜像中预编辑的起点（= 替换区起点）。</summary>
    public int DisplayPreeditStart => Math.Clamp(Replacement.Start, 0, Committed.Length);

    /// <summary>显示镜像中预编辑的终点（不含）。</summary>
    public int DisplayPreeditEnd => DisplayPreeditStart + _preedit.Length;

    /// <summary>
    /// 显示镜像偏移 → 已提交文档偏移。
    /// </summary>
    /// <remarks>
    /// 落在预编辑内部时返回替换区起点（预编辑在已提交文档里不占位置）；落在后缀时按预编辑与替换区的
    /// 长度差平移。越界偏移先夹取到显示串范围内。
    /// </remarks>
    public int DisplayToCommitted(int displayOffset)
    {
        var text = Committed.Text;
        var start = DisplayPreeditStart;
        var preeditEnd = DisplayPreeditEnd;
        var offset = Math.Clamp(displayOffset, 0, DisplayLength);

        if (offset <= start)
        {
            return offset;
        }

        if (offset < preeditEnd)
        {
            // 预编辑内部：已提交文档里没有对应位置，折叠到替换区起点。
            return start;
        }

        // 预编辑终点及其后属于已提交后缀：按预编辑与替换区的长度差平移。
        var suffixOffset = offset - preeditEnd;
        return Math.Min(Math.Clamp(Replacement.End, 0, text.Length) + suffixOffset, text.Length);
    }

    /// <summary>
    /// 已提交文档偏移 → 显示镜像偏移。
    /// </summary>
    /// <remarks>
    /// 落在替换区内部（已提交坐标）时折叠到预编辑起点：被替换掉的已提交文本在显示串里已经不存在，
    /// 不能返回一个“指向预编辑中间”的伪位置。
    /// </remarks>
    public int CommittedToDisplay(int committedOffset)
    {
        var text = Committed.Text;
        var offset = Math.Clamp(committedOffset, 0, text.Length);
        var start = Math.Clamp(Replacement.Start, 0, text.Length);
        var end = Math.Max(start, Math.Clamp(Replacement.End, 0, text.Length));

        if (offset <= start)
        {
            return offset;
        }

        if (offset < end)
        {
            // 严格位于替换区内部：显示串里已被预编辑取代，折叠到预编辑起点。
            return start;
        }

        // 替换区终点及其后是已提交后缀：按预编辑与替换区的长度差平移。
        // 终点本身映射到预编辑之后（与 DisplayToCommitted 互为逆运算）。
        return offset + _preedit.Length - (end - start);
    }

    /// <summary>该已提交偏移是否落在替换区内部（含起点、不含终点）。</summary>
    public bool IsInsideReplacement(int committedOffset)
        => committedOffset >= Replacement.Start && committedOffset < Replacement.End;

    /// <summary>已提交选区在显示镜像中的范围（端点各自映射）。</summary>
    public TextRange CommittedSelectionInDisplay()
    {
        var range = Committed.Selection.Range;
        var start = CommittedToDisplay(range.Start);
        var end = CommittedToDisplay(range.End);
        return end < start ? new TextRange(end, start) : new TextRange(start, end);
    }

    /// <summary>只输出长度/版本/范围，不输出用户原文。</summary>
    public override string ToString()
        => $"v{Committed.Version} committedLen={CommittedLength} displayLen={DisplayLength} "
            + $"replacement={Replacement} preeditLen={_preedit.Length} "
            + $"mirror={MirrorRevision} native={NativeRevision}"
            + (IsComposing ? " composing" : string.Empty);

    /// <summary>由适配器构造（只有适配器知道真实状态与修订号）。</summary>
    internal static TextInputAuthoritativeSnapshot Create(
        TextSnapshot committed,
        TextRange replacement,
        string? preedit,
        int preeditCaret,
        IReadOnlyList<TextRange>? segments,
        long mirrorRevision,
        long nativeRevision,
        bool composing)
    {
        var text = committed.Text;
        var start = Math.Clamp(replacement.Start, 0, text.Length);
        var end = Math.Max(start, Math.Clamp(replacement.End, 0, text.Length));
        var frozen = Freeze(segments);
        var value = preedit ?? string.Empty;
        var caret = Math.Clamp(preeditCaret, 0, value.Length);
        return new TextInputAuthoritativeSnapshot(
            committed,
            new TextRange(start, end),
            value,
            caret,
            frozen,
            mirrorRevision,
            nativeRevision,
            composing);
    }

    /// <summary>
    /// 把任意只读列表复制为不可变集合：既防御输入列表被事后改写，也防止消费者修改已发布快照。
    /// </summary>
    private static ImmutableArray<TextRange> Freeze(IReadOnlyList<TextRange>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TextRange>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            builder.Add(segments[i]);
        }

        return builder.MoveToImmutable();
    }
}

/// <summary>
/// 一段组合的<b>不可复用</b>身份票据（TI-07 P2a，R1）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="TextInputCompositionSession.Begin"/> 在核心真正接受组合后创建，之后所有 Update/Commit/Cancel/End
/// 都必须携带同一个实例。适配器用<b>引用相等</b>校验：旧组合的票据永远不等于新组合的票据，因此
/// “旧组合结束后开新组合、旧结果迟到”无法被套上新身份。
/// </para>
/// <para>
/// 票据一次性：组合结束/取消/失效即退休，再次使用显式拒绝。票据不按内容或时间比较，也不可伪造
/// （构造函数 internal 且只有适配器调用）。
/// </para>
/// <para><b>诊断不含用户原文：</b><see cref="ToString"/> 只输出身份编号。</para>
/// </remarks>
internal sealed class TextInputCompositionToken
{
    private int _retired;

    internal TextInputCompositionToken(int epoch, int compositionId, long createdByOperationId)
    {
        Epoch = epoch;
        CompositionId = compositionId;
        CreatedByOperationId = createdByOperationId;
    }

    /// <summary>创建该票据的焦点 epoch。</summary>
    internal int Epoch { get; }

    /// <summary>该票据对应的组合 ID。</summary>
    internal int CompositionId { get; }

    /// <summary>创建该票据的 Begin 操作编号。</summary>
    internal long CreatedByOperationId { get; }

    /// <summary>票据是否已退休（组合结束/取消/失效）。</summary>
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>最近一次被接受的操作编号（单调水位，用于拒绝重复投递的同一原生确认）。</summary>
    internal long LastAcceptedOperationId { get; private set; }

    /// <summary>退休该票据（幂等）。</summary>
    internal void Retire() => Volatile.Write(ref _retired, 1);

    /// <summary>
    /// 该操作编号是否是可接受的新操作：必须严格大于已接受水位。
    /// </summary>
    /// <remarks>同一原生确认被重复投递时编号相同，因此被拒绝；两次合法的相同结果各有自己的编号，都能提交。</remarks>
    internal bool TryAccept(long operationId)
    {
        if (operationId <= LastAcceptedOperationId)
        {
            return false;
        }

        LastAcceptedOperationId = operationId;
        return true;
    }

    /// <summary>只输出身份编号，不输出用户内容。</summary>
    public override string ToString()
        => $"token(epoch={Epoch} composition={CompositionId} createdBy={CreatedByOperationId}"
            + (IsRetired ? " retired" : string.Empty) + ")";
}

/// <summary>
/// 一次适配器操作的显式结论（TI-07 P2a）：不抛异常、不静默乐观成功、也不抹掉已发生的事实。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Status"/> 区分四类结论：
/// <see cref="TextInputAdapterStatus.Applied"/>（真实完成）、
/// <see cref="TextInputAdapterStatus.NoOp"/>（幂等/无变化）、
/// <see cref="TextInputAdapterStatus.Rejected"/>（<b>没有任何动作</b>）、
/// <see cref="TextInputAdapterStatus.AppliedSynchronizationFailed"/>
/// （<b>核心已确认执行，但适配器随后无法同步真实文档</b>）。
/// </para>
/// <para>
/// <b>R7 关键语义：</b>后置回读失败或归属变化发生在 <see cref="ITextInputClient.ApplyComposition"/> 已经返回
/// 真实结果之后，此时 <see cref="ApplyResult"/> 仍原样保留（含 <see cref="Handled"/>、
/// <see cref="TextInputOutcome.ResultCommitted"/>、<see cref="TextInputApplyResult.AppliedEventCount"/>
/// 与权威 <see cref="TextInputApplyResult.Document"/>）。宿主据此仍能正确释放字符回声，
/// 不会因为适配器同步失败而重复插入。这类结论<b>不是</b> <see cref="IsRejected"/>。
/// </para>
/// <para>
/// 反过来，<see cref="IsRejected"/> 严格表示“零动作”：<see cref="ApplyResult"/> 为 <c>default</c>，
/// <see cref="Handled"/> 与 <see cref="Committed"/> 均为 false，且从未调用过 <c>ApplyComposition</c>。
/// </para>
/// <para><see cref="Token"/> 只在 Begin 成功时非 null：调用方必须保存它并在后续操作中携带。</para>
/// <para><see cref="Reason"/> 说明拒绝或同步失败的原因，且<b>不含用户原文</b>。</para>
/// </remarks>
internal readonly record struct TextInputAdapterResult
{
    private TextInputAdapterResult(
        TextInputAdapterStatus status,
        TextInputAuthoritativeSnapshot snapshot,
        TextInputApplyResult applyResult,
        string? reason,
        TextInputCompositionToken? token)
    {
        Status = status;
        Snapshot = snapshot;
        ApplyResult = applyResult;
        Reason = reason;
        Token = token;
    }

    /// <summary>结论类别。</summary>
    public TextInputAdapterStatus Status { get; }

    /// <summary>
    /// 操作之后的权威快照。
    /// </summary>
    /// <remarks>
    /// 同步失败时这里使用<b>已确认的</b> <see cref="TextInputApplyResult.Document"/>（核心回读的权威结果），
    /// 不是适配器猜测的版本递增；预编辑镜像已撤销，因此不会出现“旧预编辑 + 新文档”的混合。
    /// </remarks>
    public TextInputAuthoritativeSnapshot Snapshot { get; }

    /// <summary>
    /// 框架返回的真实批次结果；只有从未投递事件时才为 <c>default</c>。
    /// </summary>
    /// <remarks>
    /// 同步失败时仍保留真实结果——调用方必须据此判断是否已经发生过写入。
    /// </remarks>
    public TextInputApplyResult ApplyResult { get; }

    /// <summary>拒绝或同步失败的原因（脱敏）；完全成功时为 null。</summary>
    public string? Reason { get; }

    /// <summary>Begin 成功时返回的组合票据；其它情况为 null。</summary>
    public TextInputCompositionToken? Token { get; }

    /// <summary>事件是否已被框架消费（有事件投递且真实应用了前缀）。</summary>
    /// <remarks>同步失败时仍可能为 true：这正是“已执行、但适配器无法继续同步”的情形。</remarks>
    public bool Handled => ApplyResult.Handled;

    /// <summary>是否被显式拒绝（<b>没有任何动作</b>，从未投递事件）。</summary>
    public bool IsRejected => Status == TextInputAdapterStatus.Rejected;

    /// <summary>核心是否已确认执行、但适配器随后无法同步真实文档（需要调用方降级处理）。</summary>
    public bool IsAppliedButUnsynchronized
        => Status == TextInputAdapterStatus.AppliedSynchronizationFailed;

    /// <summary>是否真实提交了一段结果。</summary>
    /// <remarks>同步失败时仍如实反映已发生的提交，绝不因为回读失败而变成 false。</remarks>
    public bool Committed => (ApplyResult.Outcome & TextInputOutcome.ResultCommitted) != 0;

    internal static TextInputAdapterResult Applied(
        TextInputAdapterStatus status,
        TextInputAuthoritativeSnapshot snapshot,
        TextInputApplyResult applyResult,
        TextInputCompositionToken? token = null)
        => new(status, snapshot, applyResult, null, token);

    internal static TextInputAdapterResult NoOp(TextInputAuthoritativeSnapshot snapshot, string? reason = null)
        => new(TextInputAdapterStatus.NoOp, snapshot, default, reason, null);

    internal static TextInputAdapterResult Rejected(TextInputAuthoritativeSnapshot snapshot, string reason)
        => new(TextInputAdapterStatus.Rejected, snapshot, default, reason, null);

    /// <summary>
    /// 核心已确认执行、但适配器无法同步真实文档（R7）：保留真实 <paramref name="applyResult"/>，
    /// 明确报告“已执行动作 + 同步失败”，不假称未执行。
    /// </summary>
    internal static TextInputAdapterResult AppliedSynchronizationFailed(
        TextInputAuthoritativeSnapshot snapshot,
        TextInputApplyResult applyResult,
        string reason,
        TextInputCompositionToken? token = null)
        => new(TextInputAdapterStatus.AppliedSynchronizationFailed, snapshot, applyResult, reason, token);

    public override string ToString()
        => $"{Status} {Snapshot} handled={Handled} committed={Committed}"
            + (Reason is null ? string.Empty : $" ({Reason})");
}

/// <summary>适配器操作的结论类别。</summary>
internal enum TextInputAdapterStatus
{
    /// <summary>真实执行了请求的动作（可能仍无写入，例如结果被 setter 拒绝）。</summary>
    Applied = 0,

    /// <summary>幂等/无变化：请求已被安全忽略（重复 End、重复结果、同值预编辑等）。</summary>
    NoOp = 1,

    /// <summary>显式拒绝：epoch/版本/归属不符、重入、非法负载或客户端已撤销；<b>没有任何动作</b>。</summary>
    Rejected = 2,

    /// <summary>
    /// 核心已确认执行（<see cref="TextInputApplyResult.Handled"/> 为 true，可能已提交结果），
    /// 但适配器随后无法同步真实文档（后置回读失败/归属变化）。真实结果被保留。
    /// </summary>
    AppliedSynchronizationFailed = 3,
}
