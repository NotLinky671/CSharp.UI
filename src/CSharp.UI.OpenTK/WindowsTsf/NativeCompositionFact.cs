namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 一条<b>原生事实</b>的来源：明确区分 COM 直接提供的数据与观察器推断的策略。
/// </summary>
/// <remarks>
/// 报告与测试按此区分“事实”与“策略”：只有 <see cref="NativeCompositionFactSource"/> 标注为 COM 的字段
/// 才是 TSF 直接给出的；组合起止/结果的<b>语义分类</b>一律是策略，且必须可被单独质疑。
/// </remarks>
internal enum NativeCompositionFactSource
{
    /// <summary>由 COM 回调参数直接提供（无需推断）。</summary>
    Com,

    /// <summary>由适配器按既有规则从 COM 数据推导（策略，需单独审核）。</summary>
    Derived,
}

/// <summary>
/// 一次原生组合/编辑的不可变事实快照（TI-07 P2b 第二批）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只含事实：</b>所有范围、文本、标志都来自 COM 回调当场复制的值；本类型<b>不</b>持有任何
/// COM 接口指针、不持有可借用 range、不保存 ITfContext——借用范围在回调返回后即失效。
/// </para>
/// <para>
/// <b>不可变：</b>构造后只读；<see cref="Preedit"/> 为冻结字符串。
/// </para>
/// <para><b>诊断不含用户原文：</b><see cref="ToString"/> 只输出偏移、长度与标志。</para>
/// </remarks>
internal sealed class NativeCompositionFact
{
    private readonly string _preedit;

    internal NativeCompositionFact(
        NativeCompositionFactKind kind,
        long nativeGeneration,
        int ownerEpoch,
        int anchor,
        int length,
        string? preedit,
        bool hasRange,
        bool selectionChanged,
        bool textChanged,
        bool hasResidualPreedit,
        NativeCompositionFactSource rangeSource)
    {
        Kind = kind;
        NativeGeneration = nativeGeneration;
        OwnerEpoch = ownerEpoch;
        Anchor = anchor;
        Length = length;
        _preedit = preedit ?? string.Empty;
        HasRange = hasRange;
        SelectionChanged = selectionChanged;
        TextChanged = textChanged;
        HasResidualPreedit = hasResidualPreedit;
        RangeSource = rangeSource;
    }

    /// <summary>事实类别。</summary>
    public NativeCompositionFactKind Kind { get; }

    /// <summary>产生该事实时的原生代数（隔离陈旧事实）。</summary>
    public long NativeGeneration { get; }

    /// <summary>产生该事实时的 owner epoch（隔离旧焦点会话）。</summary>
    public int OwnerEpoch { get; }

    /// <summary>组合起点（ACP，UTF-16 码元）。</summary>
    public int Anchor { get; }

    /// <summary>组合长度（ACP，UTF-16 码元）。</summary>
    public int Length { get; }

    /// <summary>组合范围终点的 ACP 偏移。</summary>
    public int End => Anchor + Length;

    /// <summary>该范围是否真实可用（false 表示原生未给出有效范围——显式失败，不伪造 caret）。</summary>
    public bool HasRange { get; }

    /// <summary>原生组合文本（预编辑候选）；非组合事实为空串。</summary>
    public string Preedit => _preedit;

    /// <summary>编辑完成记录是否报告选区变化。</summary>
    public bool SelectionChanged { get; }

    /// <summary>编辑完成记录是否报告文本变化。</summary>
    public bool TextChanged { get; }

    /// <summary>
    /// 该事实是否携带“仍有剩余预编辑”的语义（部分确认后继续组合）。
    /// </summary>
    /// <remarks>
    /// 这是<b>策略</b>：由 owner 按“结果之后仍有活动组合范围”推导，不是 COM 直接给出的标志。
    /// </remarks>
    public bool HasResidualPreedit { get; }

    /// <summary>范围数据的来源（COM 直接提供 or 推导）。</summary>
    public NativeCompositionFactSource RangeSource { get; }

    /// <summary>
    /// 捕获该事实时的原生文档文本快照（仅完成编辑事实使用）。
    /// </summary>
    /// <remarks>
    /// 必须在<b>捕获时</b>冻结：多条完成编辑事实排队时，安全阶段统一读取会读到同一份最终文本，
    /// 从而把两次真实事务合并成一次（丢一次提交）。
    /// </remarks>
    internal string? CapturedNativeText { get; init; }

    /// <summary>
    /// 捕获该事实时的活动组合范围（原生坐标）。
    /// </summary>
    /// <remarks>
    /// <b>必须冻结：</b>多个事实在 drain 之前被连续捕获时，若在 drain 阶段读“当前”状态，
    /// 所有事实都会看到最终状态，导致两次真实事务被合并、或纯预编辑被误判为提交。
    /// </remarks>
    internal (int Anchor, int Length)? CapturedActiveRange { get; init; }

    /// <summary>捕获该事实时是否存在“已终止待定论”的组合。</summary>
    internal bool CapturedTerminal { get; init; }

    /// <summary>
    /// 捕获该事实时的原生<b>有向</b>选区（原生/显示镜像坐标，保留反向）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须冻结：</b>完成事务的选区是“该事务发生时”的原生状态。若在安全阶段读取全局选区，
    /// 排队事务会共用最后一次状态，导致前面的选区被覆盖、方向丢失。
    /// </para>
    /// <para>
    /// <b>保留方向：</b>锚点/活动端分别保存，绝不用排序后的范围替代——
    /// 反向选中 3→1 与 caret 在 1 在规范化范围里完全相同。
    /// </para>
    /// <para>仅当该事实确实报告了选区变化时才非 null。</para>
    /// </remarks>
    internal TextSelection? CapturedSelection { get; init; }

    /// <summary>
    /// 捕获该事实时自有窗口是否已离开前台焦点：<c>true</c>=已离开（物理失活段事实）、
    /// <c>false</c>=仍在（正常/独立结果）、<c>null</c>=未判定（无探针环境）。
    /// </summary>
    /// <remarks>
    /// 失活栅栏归因判据：<see cref="NativeCompositionOwner.CaptureDeactivationProbe"/> 在捕获时刻
    /// 采样 <c>GetFocus/GetForegroundWindow</c> 对自有 hwnd——TIP 经 TSF focus 通道的失活 capture
    /// 可在任何窗口消息之前到达（minimize/hide 取证），ticket 序无法归因；物理前台/焦点
    /// 状态是捕获源的真实身份。
    /// </remarks>
    internal bool? CapturedDeactivated { get; init; }

    /// <summary>只输出偏移、长度与标志，不输出用户原文。</summary>
    public override string ToString()
        => $"{Kind} gen={NativeGeneration} epoch={OwnerEpoch} "
            + (HasRange ? $"[{Anchor},{End})" : "(no-range)")
            + $" preeditLen={_preedit.Length}"
            + (SelectionChanged ? " sel" : string.Empty)
            + (TextChanged ? " text" : string.Empty)
            + (HasResidualPreedit ? " residual" : string.Empty);
}

/// <summary>原生事实类别（对应报告中的事件/事务表行）。</summary>
internal enum NativeCompositionFactKind
{
    /// <summary>组合开始（<c>ITfContextOwnerCompositionSink::OnStartComposition</c>）。</summary>
    Start = 0,

    /// <summary>组合更新（<c>OnUpdateComposition</c>，含同长度替换）。</summary>
    Update = 1,

    /// <summary>组合结束（<c>OnEndComposition</c>；<b>无</b>取消标志）。</summary>
    End = 2,

    /// <summary>编辑事务结束（<c>ITfTextEditSink::OnEndEdit</c>，已验证的“编辑完成”边界）。</summary>
    EditCompleted = 3,

    /// <summary>
    /// <b>显式取消</b>：由了解 TSF 终止语义的调用方声明。
    /// </summary>
    /// <remarks>
    /// 不从 <c>OnEndComposition</c> 推断（该回调<b>没有</b>取消标志），也不按文本是否为空判断。
    /// </remarks>
    Cancel = 4,
}

/// <summary>
/// 原生事实的<b>策略</b>分类结果（可被独立审核；不是 COM 事实）。
/// </summary>
internal enum NativeCompositionAction
{
    /// <summary>开始/更新组合（映射到 P2a Begin / UpdatePreedit）。</summary>
    Preedit = 0,

    /// <summary>确认一段结果（映射到 P2a CommitResult）。</summary>
    Commit = 1,

    /// <summary>取消组合（映射到 P2a Cancel）。</summary>
    Cancel = 2,

    /// <summary>结束组合但无结果（映射到 P2a End）。</summary>
    End = 3,

    /// <summary>忽略（陈旧代数/无活动组合/无需处理）。</summary>
    Ignore = 4,

    /// <summary>显式拒绝（原生数据不可用/非法）：不写入、不伪造成功。</summary>
    Reject = 5,

    /// <summary>
    /// 原生选区（映射到 P2a <c>SetNativeSelection</c>）：不写 Binding，只移动 caret/选区。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Commit"/>/<see cref="Preedit"/> 并列，因为同一个完成事务可以同时有文本动作和选区动作；
    /// 顺序固定为“先文本、后选区”。
    /// </remarks>
    Selection = 6,
}

