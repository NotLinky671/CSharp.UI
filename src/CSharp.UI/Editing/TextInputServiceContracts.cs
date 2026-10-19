namespace CSharp.UI;

/// <summary>
/// 文本输入几何：候选锚点所需的有效 caret 与组合矩形，坐标为窗口内容区逻辑像素。
/// </summary>
/// <remarks>
/// <para>
/// 逻辑像素 → 屏幕/客户区单位的换算由宿主负责（<c>CSharp.UI.OpenTK</c> 使用现有的窗口/逻辑/framebuffer
/// 三空间转换）；核心契约只描述逻辑像素，不引入 HWND/NSView/平台矩形类型。
/// </para>
/// <para>
/// 几何必须来自最终排版原点 + 真实 caret，包含横向滚动、祖先滚动、Padding/Frame 与像素吸附；
/// 被完全裁出或隐藏时，客户端应撤销锚点（发布空矩形）而不是把候选定位到窗口原点。
/// </para>
/// </remarks>
public readonly record struct TextInputGeometry
{
    /// <summary>构造文本输入几何。</summary>
    /// <param name="caretBounds">已提交文本的 caret 矩形。</param>
    /// <param name="compositionBounds">预编辑（组合）矩形；无组合时为 <see cref="Rect.Empty"/>。</param>
    /// <param name="exclusionBounds">系统候选窗需要避让的区域；无要求时为 <see cref="Rect.Empty"/>。</param>
    public TextInputGeometry(Rect caretBounds, Rect compositionBounds = default, Rect exclusionBounds = default)
    {
        CaretBounds = caretBounds;
        CompositionBounds = compositionBounds;
        ExclusionBounds = exclusionBounds;
    }

    /// <summary>
    /// 有效逻辑 caret 矩形（逻辑像素）：组合期间为组合 caret，否则为已提交文本 caret；
    /// 已与祖先裁剪/内容区裁剪相交，完全不可见时整体为 <see cref="Unavailable"/>。
    /// </summary>
    public Rect CaretBounds { get; }

    /// <summary>预编辑矩形（逻辑像素）；无组合时为空矩形。</summary>
    public Rect CompositionBounds { get; }

    /// <summary>排除区（逻辑像素）；无要求时为空矩形。</summary>
    public Rect ExclusionBounds { get; }

    /// <summary>caret 矩形是否有效（具有正的纵向范围）。</summary>
    public bool HasCaret => CaretBounds.Height > 0;

    /// <summary>是否存在可见的预编辑几何。</summary>
    public bool HasComposition => !CompositionBounds.IsEmpty;

    /// <summary>不可发布的几何（客户端被完全裁出/隐藏时使用）。</summary>
    public static TextInputGeometry Unavailable => default;

    public override string ToString()
        => $"caret={CaretBounds}{(HasComposition ? $" composition={CompositionBounds}" : string.Empty)}";
}

/// <summary>
/// 当前焦点文本输入状态（提交给输入服务的只读视图）：文档快照、预编辑串与几何。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Document"/> 的 <c>Text</c> 始终只包含“已提交”文本；预编辑放在 <see cref="Preedit"/>，
/// 因此输入服务/平台桥不会把未提交内容当成文档。预编辑期间的显示串（已提交前缀 + 替换区预编辑 +
/// 已提交后缀）必须按当前组合身份的替换区构造，由 TextField 绘制阶段负责（TI-04/TI-06）；
/// 本阶段不提供“文档 + 预编辑”这类会误导替换语义的快捷属性。
/// </para>
/// <para>
/// <b>default 安全与诊断脱敏：</b><see cref="Preedit"/> 对 <c>default</c> 返回空串；
/// <see cref="ToString"/> 只输出长度与版本，不输出用户原文。
/// </para>
/// </remarks>
public readonly record struct TextInputState
{
    private readonly string? _preedit;

    /// <summary>构造文本输入状态。</summary>
    /// <param name="document">已提交文档快照。</param>
    /// <param name="preedit">当前预编辑串；null 视为空串。</param>
    /// <param name="geometry">候选锚点几何。</param>
    /// <param name="composing">是否处于组合状态。</param>
    public TextInputState(TextSnapshot document, string? preedit = null, TextInputGeometry geometry = default, bool composing = false)
    {
        Document = document;
        _preedit = preedit ?? string.Empty;
        Geometry = geometry;
        IsComposing = composing;
    }

    /// <summary>已提交文档快照（含版本与选区）。</summary>
    public TextSnapshot Document { get; }

    /// <summary>未提交的预编辑串；default 状态返回空串而不是 null。</summary>
    public string Preedit => _preedit ?? string.Empty;

    /// <summary>候选锚点几何。</summary>
    public TextInputGeometry Geometry { get; }

    /// <summary>是否处于组合状态。</summary>
    public bool IsComposing { get; }

    /// <summary>只输出版本/长度与组合标记，不输出用户原文。</summary>
    public override string ToString()
        => $"{Document}{(IsComposing ? $" composing preeditLen={Preedit.Length}" : string.Empty)}";
}

/// <summary>
/// 焦点文本输入会话身份：一次“某节点获得焦点”的 epoch 与该 epoch 下单调递增的组合 ID。
/// </summary>
/// <remarks>
/// <para>
/// 切换焦点、窗口失活、节点卸载/禁用或绑定来源替换都会开启新的 <see cref="SessionId"/>（epoch）。
/// 旧 epoch 的事件即使格式合法也不再写入新客户端；这是“不串写”的第一道隔离。
/// </para>
/// <para>
/// <see cref="CompositionId"/> 在会话内单调递增，保证同一 epoch 内先后两段组合不会因为 ID 相同而被误认。
/// 具体的事件归属比较与过时抑制由 TI-04 的组合状态机执行。
/// </para>
/// </remarks>
public readonly record struct TextInputSession
{
    /// <summary>创建会话身份。</summary>
    /// <param name="sessionId">焦点会话 epoch，必须 &gt;= 0。</param>
    /// <param name="compositionId">下一次组合使用的 ID，必须 &gt;= 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一值为负。</exception>
    public TextInputSession(int sessionId, int compositionId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(compositionId);
        SessionId = sessionId;
        CompositionId = compositionId;
    }

    /// <summary>焦点会话 epoch。</summary>
    public int SessionId { get; }

    /// <summary>本会话可用的下一个组合 ID。</summary>
    public int CompositionId { get; }

    /// <summary>开启下一个 epoch（会话 ID 递增、组合 ID 归零）。</summary>
    public TextInputSession NextSession() => new(SessionId + 1, 0);

    /// <summary>在当前 epoch 内取用下一个组合 ID。</summary>
    public TextInputSession NextComposition() => new(SessionId, CompositionId + 1);

    /// <summary>使用指定组合 ID 并推进到其后一个。</summary>
    public TextInputSession WithComposition(int compositionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(compositionId);
        return new TextInputSession(SessionId, Math.Max(CompositionId, compositionId + 1));
    }

    public override string ToString() => $"session={SessionId} nextComposition={CompositionId}";
}

/// <summary>
/// 输入服务<b>实际完成</b>的动作（能力位集）；未执行的动作不得出现在这里。
/// </summary>
/// <remarks>
/// <para>
/// 调用方（宿主）据此决定字符所有权：只有当 <see cref="ResultCommitted"/> 出现时，才认为提交文本已经由
/// 框架落地；只有 <see cref="Started"/> / <see cref="Updated"/> 表示组合仍在进行。
/// </para>
/// <para>
/// 请求阶段与实际动作必须区分：请求里出现了 Result 但服务没有能力/没有客户端时，这里只有
/// <see cref="Rejected"/>，不得报告 <see cref="ResultCommitted"/>。本批次“请求了哪些阶段”由调用方
/// 直接读取 <see cref="CompositionEvent.Kind"/> 判断，不额外引入阶段位集。
/// </para>
/// </remarks>
[Flags]
public enum TextInputOutcome
{
    /// <summary>未执行任何动作（无能力、无客户端或事件被拒绝）。</summary>
    None = 0,

    /// <summary>已开始/继续组合，尚未提交。</summary>
    Started = 1 << 0,

    /// <summary>已更新预编辑。</summary>
    Updated = 1 << 1,

    /// <summary>已提交一段结果（产生一次原子事务）。</summary>
    ResultCommitted = 1 << 2,

    /// <summary>已取消未提交的组合。</summary>
    Cancelled = 1 << 3,

    /// <summary>组合已结束。</summary>
    Ended = 1 << 4,

    /// <summary>输入被显式拒绝（能力缺失、参数非法、无客户端）；未执行任何动作。</summary>
    Rejected = 1 << 5,
}

/// <summary>
/// 一次组合批次被应用后的同步结果：宿主据此决定是否阻止默认重复插入。</summary>
/// <param name="Outcome">服务<b>实际完成</b>的动作集合；未执行的动作不会出现。</param>
/// <param name="Document">应用后的文档快照；未处理时等于传入的快照。</param>
/// <param name="Handled">
/// 事件是否已由框架同步消费。为 true 时宿主不得再把同一原生消息的字符插入文档；
/// 为 false 时框架没有消费任何事件，宿主也不得把该批次“重投”到当前焦点——
/// 已提交字符始终走既有 <see cref="UiRuntime.DispatchTextInput"/> 通道，组合批次不做重放。
/// <para>
/// <b>部分批次语义：</b>批次允许已处理前缀。后续事件被拒绝时，只要前缀有事件被消费，这里就为 true——
/// 不能用 false 宣称整批未处理，否则宿主会把已由框架落地的字符再插入一次。
/// </para>
/// </param>
/// <param name="RejectionReason">未处理或部分处理时的诊断原因；不包含用户原文。</param>
/// <param name="AppliedEventCount">
/// 本批次中<b>实际处理</b>的事件数量（含幂等无写入的 Cancel/End）。批次允许已处理前缀：后续事件被拒绝时
/// 这个计数如实反映已执行的部分，不谎报整批未处理（未处理时为 0）。
/// </param>
public readonly record struct TextInputApplyResult(
    TextInputOutcome Outcome,
    TextSnapshot Document,
    bool Handled,
    string? RejectionReason = null,
    int AppliedEventCount = 0)
{
    /// <summary>未执行任何动作（无能力/无客户端/格式非法）的结果。</summary>
    public static TextInputApplyResult NotHandled(TextSnapshot document, string? reason = null)
        => new(TextInputOutcome.None, document, false, reason);

    /// <summary>显式拒绝且未执行任何动作的结果。</summary>
    public static TextInputApplyResult Rejected(TextSnapshot document, string? reason)
        => new(TextInputOutcome.Rejected, document, false, reason);

    /// <summary><see cref="Outcome"/> 是否只表示“没有执行任何动作”。</summary>
    public bool IsNoOp => (Outcome & ~TextInputOutcome.Rejected) == TextInputOutcome.None;

    /// <summary>批次中有事件被拒绝（可能同时已经执行了前缀动作）。</summary>
    public bool HasRejection => (Outcome & TextInputOutcome.Rejected) != 0;

    public override string ToString()
        => $"{Outcome} handled={Handled} applied={AppliedEventCount}"
            + (RejectionReason is null ? string.Empty : $" ({RejectionReason})");
}

/// <summary>
/// 平台文本输入能力报告：已提交文本、组合输入、候选锚点分别声明；缺失与失败有可观察诊断。
/// </summary>
/// <remarks>
/// <para>
/// 三个能力互相独立：<see cref="Composition"/> 或 <see cref="CandidateAnchor"/> 为 true 必然意味着
/// <see cref="CommittedText"/> 也为 true（没有已提交文本通道就无法落地结果），构造期强制该一致性。
/// </para>
/// <para>
/// 平台桥（TI-07/TI-08）必须如实报告；<see cref="Reason"/> 用于记录缺失原因（例如“XIM 预编辑在 GLFW 内部消费，
/// 未暴露”），也可作为可观察诊断由宿主写入运行时诊断。
/// </para>
/// </remarks>
public readonly record struct TextInputCapabilities
{
    /// <summary>构造能力报告并校验一致性。</summary>
    /// <param name="committedText">是否支持已提交文本（宿主已提交字符或框架的已提交文本通道）。</param>
    /// <param name="composition">是否支持组合（预编辑）输入。</param>
    /// <param name="candidateAnchor">是否支持系统候选窗锚点。</param>
    /// <param name="reason">诊断原因；null 表示无需说明。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="committedText"/> 为 false 却声明了组合或候选锚点能力。
    /// </exception>
    public TextInputCapabilities(bool committedText, bool composition = false, bool candidateAnchor = false, string? reason = null)
    {
        if (!committedText && (composition || candidateAnchor))
        {
            throw new ArgumentException(
                "组合输入与候选锚点能力都隐含已提交文本能力；committedText=false 时不能声明它们。",
                nameof(committedText));
        }

        CommittedText = committedText;
        Composition = composition;
        CandidateAnchor = candidateAnchor;
        Reason = reason;
    }

    /// <summary>是否支持已提交文本。</summary>
    public bool CommittedText { get; }

    /// <summary>是否支持组合（预编辑）输入。</summary>
    public bool Composition { get; }

    /// <summary>是否支持系统候选窗锚点。</summary>
    public bool CandidateAnchor { get; }

    /// <summary>诊断原因；null 表示无需说明。</summary>
    public string? Reason { get; }

    /// <summary>完全无文本输入能力（无服务/初始化失败）。</summary>
    public static TextInputCapabilities None { get; } = new(
        false,
        reason: "没有可用的文本输入服务或初始化失败。");

    /// <summary>只有已提交文本（macOS/Linux 回退与无窗口宿主）。</summary>
    public static TextInputCapabilities CommittedTextOnly { get; } = new(
        true,
        reason: "当前宿主只提供已提交文本；组合输入与候选锚点未接入。");

    /// <summary>完整能力（已提交文本 + 组合 + 候选锚点）。</summary>
    public static TextInputCapabilities Full { get; } = new(true, true, true);

    public override string ToString()
        => $"committed={CommittedText} composition={Composition} anchor={CandidateAnchor}"
            + (Reason is null ? string.Empty : $" ({Reason})");
}

/// <summary>
/// 文本输入客户端：由当前获得焦点的可编辑控件实现（TI-03），向输入服务暴露文档与接收组合事件。
/// </summary>
/// <remarks>
/// <para>
/// <b>所有权：</b>客户端由控件实现、只在 UI 线程被调用；服务不缓存客户端实例，只通过
/// <see cref="ITextInputService.StartSession"/> / <see cref="ITextInputService.EndSession"/> 建立与切断会话。
/// 节点卸载、禁用或失焦时，先切断旧客户端的会话归属，再允许新客户端开始会话。
/// </para>
/// <para>
/// <b>不抛异常：</b><see cref="ApplyComposition"/> 对格式非法/过时/重复事件必须返回显式失败结果，
/// 不能抛异常（原生回调路径不允许托管异常越界）。
/// </para>
/// <para>
/// <b>不修改 UI 结构：</b>客户端只读写文档与选区，不执行布局/重建，也不从 Build/Measure 被调用。
/// </para>
/// </remarks>
public interface ITextInputClient
{
    /// <summary>当前已提交文档快照（不含预编辑）。</summary>
    TextSnapshot Document { get; }

    /// <summary>当前焦点会话身份。</summary>
    TextInputSession Session { get; }

    /// <summary>当前是否处于未提交的组合状态。</summary>
    bool IsComposing { get; }

    /// <summary>当前预编辑串；无组合时为空串。</summary>
    string Preedit { get; }

    /// <summary>向服务发布当前输入状态（文档、预编辑与几何）。</summary>
    TextInputState GetState();

    /// <summary>
    /// 同步应用一个组合事件批次，并返回<b>实际完成</b>的动作与 <see cref="TextInputApplyResult.Handled"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>顺序契约（TI-04 实际执行）：</b>一段组合的顺序是
    /// <c>Start → Update* → (Result | Cancel) → End</c>，可以跨多条原生消息，因此后续消息的批次不要求带 Start；
    /// 平台也存在“没有 Start 的 Result”序列。同一批次内若同时携带结果与下一段预编辑，必须按
    /// “先结果、后预编辑”的顺序处理，并以结果确认后的真实文档版本重新锚定。
    /// </para>
    /// <para>
    /// <b>过时/重复/乱序：</b>必须按会话 epoch、组合 ID 与序号隔离：epoch 不匹配、序号倒退、
    /// 已结束组合的重复事件或版本锚点不匹配都不写入，返回 <c>Handled=false</c> 且
    /// <see cref="TextInputOutcome.Rejected"/>；不得把这类事件重投到当前焦点。
    /// 具体状态判定与真实事务结果（setter 拒绝、净化后无变化、MaxLength 全拒绝）由 TI-04 实现。
    /// </para>
    /// <para>
    /// <b>真实结果优先：</b>只有实际提交成功的事务才报告 <see cref="TextInputOutcome.ResultCommitted"/>；
    /// 文档版本只能按真实回读结果推进，不能因为事件类型是 Result 就假定版本 +1。
    /// </para>
    /// </remarks>
    TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events);

    /// <summary>请求客户端取消未提交的组合并回到最近提交的文档与选区。</summary>
    /// <returns>确实取消了未提交组合时返回 true；没有组合时为 false（幂等）。</returns>
    bool CancelComposition();
}

/// <summary>
/// 文本输入服务：由宿主实现（TI-07 Windows 桥、TI-08 回退），管理焦点会话、组合事件与候选锚点。
/// </summary>
/// <remarks>
/// <para>
/// <b>实现方契约：</b>
/// </para>
/// <list type="bullet">
/// <item><description>返回 <c>false</c> 表示“没有能力或没有处理”，绝不表示“已处理但结果是空”。</description></item>
/// <item><description>
/// <see cref="TextInputCapabilities.Composition"/> 为 false 时，<see cref="ApplyComposition"/> 返回
/// <see cref="TextInputOutcome.Rejected"/> 且 <c>Handled=false</c>；宿主继续用既有的已提交字符通道，
/// 组合批次不做重放。
/// </description></item>
/// <item><description>实现必须在 UI 线程被调用；不得在原生回调中递归布局或生成帧。</description></item>
/// <item><description>测试实现无窗口即可运行，因此接口不出现任何平台类型。</description></item>
/// </list>
/// <para>
/// <b>回退：</b>核心提供 <see cref="NoTextInputService"/> 与 <see cref="TextInputForwarder"/>。
/// “已提交文本”回退始终是平台既有的字符输入通道（<see cref="UiRuntime.DispatchTextInput"/>），
/// 而不是把组合批次重投到当前焦点。
/// </para>
/// </remarks>
public interface ITextInputService
{
    /// <summary>当前平台能力报告。</summary>
    TextInputCapabilities Capabilities { get; }

    /// <summary>当前绑定的客户端；没有会话时为 null。</summary>
    ITextInputClient? ActiveClient { get; }

    /// <summary>开始一个焦点文本输入会话并绑定客户端。</summary>
    /// <returns>服务接受该客户端（具备已提交文本能力）时返回 true。</returns>
    bool StartSession(ITextInputClient client);

    /// <summary>结束当前会话并解绑客户端；幂等。</summary>
    /// <returns>确实结束了会话时返回 true；本来没有会话时返回 false。</returns>
    bool EndSession();

    /// <summary>
    /// 同步应用一个组合事件批次，并返回<b>实际完成</b>的动作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不支持的实现返回 <see cref="TextInputOutcome.Rejected"/> 且 <c>Handled=false</c>；
    /// 有组合能力但当前没有活动客户端时同样只报告拒绝，调用方不得把已失去归属的结果重投到新焦点。
    /// </para>
    /// <para>
    /// 事件顺序、重复/过时抑制与“结果后重新锚定”由 TI-04 的组合状态机实现；只有真实提交成功的事务
    /// 才报告 <see cref="TextInputOutcome.ResultCommitted"/>。
    /// </para>
    /// </remarks>
    TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events);

    /// <summary>请求取消当前未提交组合；没有会话或没有组合时返回 false。</summary>
    bool RequestCancel();

    /// <summary>向平台发布当前 caret/组合几何（候选锚点）；发布后按需回读。不支持时返回 false。</summary>
    bool PublishGeometry(TextInputGeometry geometry);
}
