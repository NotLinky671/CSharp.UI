namespace CSharp.UI;

/// <summary>
/// 文本编辑的平台无关值类型（TI-02 冻结）：UTF-16 范围、带方向选区、不可变快照、编辑事务与结果。
/// </summary>
/// <remarks>
/// <para>
/// 本文件只使用框架自己的字符串/范围/值类型，不引用 OpenTK / SkiaSharp / HWND / HIMC / NSView，
/// 因此核心程序集保持平台无关。平台的字节/码点单位必须在适配层显式换算为 UTF-16（见计划§6）。
/// </para>
/// <para>
/// <b>UI 交互边界：</b>这些类型是纯数据，不认识 <c>View</c>/<c>ViewNode</c>/布局上下文，
/// 也不提供修改运行时节点的方法；唯一写入 UI 的入口是运行时的输入分发与消费方实现的编辑事务。
/// 因此“不得在 Build/Measure 里改 UI”由类型边界保证。
/// </para>
/// <para>
/// <b>校验策略（冻结，按契约种类划分）：</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>构造期显式拒绝</b>（<see cref="ArgumentException"/> / <see cref="ArgumentOutOfRangeException"/>）：
/// 负偏移、逆序端点、负版本、自相矛盾的事务结果、能力不一致。
/// 这些是编程错误，必须尽早、可定位地失败，而不是悄悄纠正后继续写错文本。
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>运行期不抛异常、返回显式判定</b>：来自平台的会话/组合数据可能乱序、重复、过时、越界或身份不匹配。
/// <see cref="CompositionEvent.TryValidatePayload(TextSnapshot, out string?)"/> 这类检查返回 false 与诊断，
/// 消费方不得写入文档。跨消息的顺序、重复与过时抑制必须由 TI-04 的组合状态机与真实编辑事务共同执行，
/// 本任务不提供该状态机。
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>default 安全性：</b><see cref="TextRange"/>、<see cref="TextSelection"/>、<see cref="TextSnapshot"/> 都是
/// <c>readonly record struct</c>，C# 保证存在 <c>default</c>。所有公开查询与失败返回都必须对 default 安全，
/// 不抛 <see cref="NullReferenceException"/>。
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>诊断不含用户原文：</b>所有 <c>ToString()</c> 只输出长度/版本/种类等元数据，不输出文本内容，
/// 遵守“日志不包含用户原文”的边界（计划§9）。
/// </description>
/// </item>
/// </list>
/// </remarks>
public readonly record struct TextRange
{
    /// <summary>由起点与终点构造（相对文本的 UTF-16 偏移，终点的含义取决于使用它的 API）。</summary>
    /// <param name="start">起点偏移，必须 &gt;= 0。</param>
    /// <param name="end">终点偏移，必须 &gt;= <paramref name="start"/>（起点等于终点表示空范围）。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 任一偏移为负，或 <paramref name="end"/> 小于 <paramref name="start"/>。逆序端点不会被静默交换，
    /// 因为方向由 <see cref="TextSelection"/> 表达，范围本身必须规范。
    /// </exception>
    public TextRange(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                end,
                $"文本范围的终点不能小于起点（start={start}, end={end}）；方向请用 TextSelection 表达。");
        }

        Start = start;
        End = end;
    }

    private TextRange(int start, int end, bool skipValidation)
    {
        Start = start;
        End = end;
    }

    /// <summary>起点 UTF-16 偏移（含）。</summary>
    public int Start { get; }

    /// <summary>终点 UTF-16 偏移（不含）。</summary>
    public int End { get; }

    /// <summary>范围内的 UTF-16 码元数量。</summary>
    public int Length => End - Start;

    /// <summary>是否为空范围（起点等于终点）。</summary>
    public bool IsEmpty => Start == End;

    /// <summary>由起点与长度构造；长度为 0 时得到空范围。</summary>
    /// <param name="start">起点偏移，必须 &gt;= 0。</param>
    /// <param name="length">长度，必须 &gt;= 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">起点或长度为负。</exception>
    public static TextRange FromLength(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new TextRange(start, start + length);
    }

    /// <summary>是否完全落在 <c>[0, textLength]</c> 之内。</summary>
    public bool IsWithin(int textLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textLength);
        return Start >= 0 && End <= textLength;
    }

    /// <summary>把范围端点夹取到 <c>[0, textLength]</c>；不改变端点顺序。</summary>
    public TextRange Clamp(int textLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textLength);
        return new TextRange(Math.Clamp(Start, 0, textLength), Math.Clamp(End, 0, textLength), skipValidation: true);
    }

    /// <summary>两个范围的交集；无交集时返回空范围 <c>[0, 0)</c>。</summary>
    public TextRange Intersect(TextRange other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return end <= start ? default : new TextRange(start, end, skipValidation: true);
    }

    /// <summary>是否包含指定偏移（含起点、不含终点）。</summary>
    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>把范围整体平移；结果可能超出文档范围，由调用方按需 <see cref="Clamp"/>。</summary>
    public TextRange Offset(int delta) => new(Start + delta, End + delta, skipValidation: true);

    /// <summary>只输出偏移，不输出文本内容。</summary>
    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>
/// 不依赖排版/渲染的字素边界判定（核心 <see cref="System.Globalization.StringInfo"/>）。
/// </summary>
/// <remarks>
/// 用于校验来自平台的范围/选区端点是否落在字素边界上（不得拆开代理对、组合字符或 ZWJ 序列）。
/// 它与 <see cref="TextLayoutResult.SnapToCaretStop"/> 的排版字素结果互补：本类型只要求核心程序集，
/// 因此在没有字体/渲染器的契约校验路径上也能使用。
/// </remarks>
internal static class TextGraphemeBoundaries
{
    /// <summary>校验所有偏移都是 <paramref name="text"/> 的字素边界；失败时返回第一个非法偏移。</summary>
    internal static bool AllAreBoundaries(string? text, ReadOnlySpan<int> offsets, out int invalidOffset)
    {
        var value = text ?? string.Empty;
        var length = value.Length;

        foreach (var offset in offsets)
        {
            if (offset < 0 || offset > length)
            {
                invalidOffset = offset;
                return false;
            }
        }

        // 快速路径：空串或端点都落在首尾时一定是边界。
        var needsLookup = false;
        foreach (var offset in offsets)
        {
            if (offset != 0 && offset != length)
            {
                needsLookup = true;
                break;
            }
        }

        if (!needsLookup)
        {
            invalidOffset = -1;
            return true;
        }

        var boundaries = new HashSet<int> { 0, length };
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            boundaries.Add(enumerator.ElementIndex + ((string)enumerator.Current).Length);
        }

        foreach (var offset in offsets)
        {
            if (!boundaries.Contains(offset))
            {
                invalidOffset = offset;
                return false;
            }
        }

        invalidOffset = -1;
        return true;
    }

    /// <summary>
    /// 把任意 UTF-16 偏移吸附到最近的字素边界（不要求排版器）；<paramref name="roundUp"/> 为 true 时向后取。
    /// </summary>
    /// <remarks>
    /// 组合回调路径使用它夹取选区：核心 <see cref="System.Globalization.StringInfo"/> 足够，避免在原生回调里
    /// 读取排版结果或递归布局。
    /// </remarks>
    internal static int SnapToBoundary(string? text, int offset, bool roundUp = false)
    {
        var value = text ?? string.Empty;
        if (offset <= 0)
        {
            return 0;
        }

        if (offset >= value.Length)
        {
            return value.Length;
        }

        var boundaries = new List<int> { 0 };
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            boundaries.Add(enumerator.ElementIndex + ((string)enumerator.Current).Length);
        }

        var found = boundaries.BinarySearch(offset);
        if (found >= 0)
        {
            return offset;
        }

        var insertion = ~found;
        return roundUp
            ? boundaries[Math.Min(insertion, boundaries.Count - 1)]
            : boundaries[Math.Max(0, insertion - 1)];
    }
}

/// <summary>选区方向：活动端相对锚点的位置，用于保留“向左/向右选择”的语义。</summary>
public enum TextSelectionDirection
{
    /// <summary>无方向（空选区）。</summary>
    None = 0,

    /// <summary>活动端在锚点之前（向左/向上选择）。</summary>
    Backward,

    /// <summary>活动端在锚点之后（向右/向下选择）。</summary>
    Forward,
}

/// <summary>
/// 带方向的 UTF-16 选区：<see cref="Anchor"/> 是选择起点，<see cref="Active"/> 是移动端（光标）。
/// </summary>
/// <remarks>
/// 两个端点都允许任意非负偏移；构造期只拒绝负值，不做越界校验。越界由快照/事务在拿到真实文本长度后
/// 用 <see cref="Clamp"/> 或 <see cref="TextRange.Clamp"/> 归一化，因此纯值类型不需要知道文档长度。
/// 归一化后端点应当落在字素边界上；字素吸附由排版结果（<see cref="TextLayoutResult"/>）与编辑事务负责。
/// </remarks>
public readonly record struct TextSelection
{
    /// <summary>由锚点与活动端构造（UTF-16 偏移，必须 &gt;= 0）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">任一偏移为负。</exception>
    public TextSelection(int anchor, int active)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(anchor);
        ArgumentOutOfRangeException.ThrowIfNegative(active);
        Anchor = anchor;
        Active = active;
    }

    /// <summary>选择锚点（不动的端点）。</summary>
    public int Anchor { get; }

    /// <summary>活动端点（随光标移动的端点）。</summary>
    public int Active { get; }

    /// <summary>折叠选区（光标位置）；方向为 <see cref="TextSelectionDirection.None"/>。</summary>
    public static TextSelection Caret(int offset) => new(offset, offset);

    /// <summary>是否为折叠选区（无选中文本）。</summary>
    public bool IsCollapsed => Anchor == Active;

    /// <summary>选中的 UTF-16 码元数量（与方向无关）。</summary>
    public int Length => Math.Abs(Active - Anchor);

    /// <summary>选区方向。</summary>
    public TextSelectionDirection Direction
        => Anchor == Active ? TextSelectionDirection.None : Active < Anchor ? TextSelectionDirection.Backward : TextSelectionDirection.Forward;

    /// <summary>规范化的选中范围 <c>[min, max)</c>。</summary>
    public TextRange Range => new(Math.Min(Anchor, Active), Math.Max(Anchor, Active));

    /// <summary>选中范围的起点（较小偏移）。</summary>
    public int SelectionStart => Range.Start;

    /// <summary>选中范围的终点（较大偏移）。</summary>
    public int SelectionEnd => Range.End;

    /// <summary>把两个端点夹取到 <c>[0, textLength]</c>，保持方向。</summary>
    public TextSelection Clamp(int textLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textLength);
        return new TextSelection(Math.Clamp(Anchor, 0, textLength), Math.Clamp(Active, 0, textLength));
    }

    /// <summary>把两个端点吸附到排版结果的字素边界（保持方向；先夹取再吸附）。</summary>
    /// <param name="layout">排版结果；必须与待编辑文本一致。</param>
    public TextSelection SnapToGraphemeBoundaries(TextLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var clamped = Clamp(layout.Text.Length);
        return new TextSelection(layout.SnapToCaretStop(clamped.Anchor), layout.SnapToCaretStop(clamped.Active));
    }

    /// <summary>把活动端移动到新偏移（先夹取到文档长度）；字素吸附由调用方通过排版结果完成。</summary>
    public TextSelection WithActive(int active, int textLength)
        => new(Anchor, Math.Clamp(active, 0, textLength));

    /// <summary>只输出偏移与方向，不输出文本内容。</summary>
    public override string ToString() => $"{Anchor}->{Active} ({Direction})";
}

/// <summary>
/// 不可变文档快照：某次操作之前/之后的文本、选区与文档版本；也用于向输入服务暴露“当前文档”。
/// </summary>
/// <remarks>
/// <para>
/// 快照一经构造不再变化；所有“操作前状态”都必须是快照而不是可变节点引用，
/// 否则事务确认与回滚会因为后续编辑而读到错的状态。
/// </para>
/// <para>
/// <b>default 安全：</b>内部把 null 文本归一化为空串，因此 <c>default(TextSnapshot)</c> 的
/// <see cref="Text"/>、<see cref="Length"/>、<see cref="IsConsistent"/> 都不会抛空引用异常。
/// </para>
/// </remarks>
public readonly record struct TextSnapshot
{
    private readonly string? _text;

    /// <summary>由文本、选区、文档版本与编辑状态构造。</summary>
    /// <param name="text">已提交文本；null 视为空串。</param>
    /// <param name="selection">选区；端点越界时不会在这里纠正，由 <see cref="IsConsistent"/> / <see cref="Clamp"/> 处理。</param>
    /// <param name="version">文档版本，必须 &gt;= 0。</param>
    /// <param name="isComposing">该快照是否处于未提交的组合状态。</param>
    /// <exception cref="ArgumentOutOfRangeException">版本为负。</exception>
    public TextSnapshot(string? text, TextSelection selection, int version = 0, bool isComposing = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        _text = text ?? string.Empty;
        Selection = selection;
        Version = version;
        IsComposing = isComposing;
    }

    /// <summary>空文档快照（空文本、光标在 0、版本 0）。</summary>
    public static TextSnapshot Empty { get; } = new(string.Empty, TextSelection.Caret(0));

    /// <summary>已提交文本（不含预编辑）；default 快照返回空串而不是 null。</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>选区（UTF-16 偏移）。</summary>
    public TextSelection Selection { get; }

    /// <summary>文档版本；每次成功提交后递增，用于隔离过时事件。</summary>
    public int Version { get; }

    /// <summary>该快照是否处于未提交的组合状态（预编辑不写入 <see cref="Text"/>）。</summary>
    public bool IsComposing { get; init; }

    /// <summary>文本的 UTF-16 长度。</summary>
    public int Length => Text.Length;

    /// <summary>选区端点是否仍在文本范围内。</summary>
    public bool IsConsistent => Selection.Anchor <= Length && Selection.Active <= Length;

    /// <summary>把选区夹取到当前文本长度。</summary>
    public TextSnapshot Clamp() => new(Text, Selection.Clamp(Length), Version, IsComposing);

    /// <summary>以新的文本与选区生成下一版本快照；版本严格递增。</summary>
    /// <param name="text">新文本；null 视为空串。</param>
    /// <param name="selection">新选区。</param>
    /// <param name="isComposing">新快照是否处于组合状态。</param>
    public TextSnapshot Next(string? text, TextSelection selection, bool isComposing = false)
        => new(text, selection, Version + 1, isComposing);

    /// <summary>返回带指定组合标记的副本，不改变文本与版本。</summary>
    public TextSnapshot WithComposing(bool isComposing) => this with { IsComposing = isComposing };

    /// <summary>只输出版本、长度与选区，不输出文本内容。</summary>
    public override string ToString()
        => $"v{Version} len={Length}{(IsComposing ? " composing" : string.Empty)} {Selection}";
}

/// <summary>编辑事务种类：所有已提交修改都归类到其中一种，便于历史合并与诊断。</summary>
public enum TextEditKind
{
    /// <summary>未指定（默认；不应出现在实际事务中）。</summary>
    None = 0,

    /// <summary>普通键入（单次提交文本）。</summary>
    Typing,

    /// <summary>粘贴。</summary>
    Paste,

    /// <summary>剪切（删除选中内容并写入剪贴板）。</summary>
    Cut,

    /// <summary>退格删除。</summary>
    Backspace,

    /// <summary>删除键删除。</summary>
    Delete,

    /// <summary>输入法结果提交。</summary>
    CompositionResult,

    /// <summary>撤销。</summary>
    Undo,

    /// <summary>重做。</summary>
    Redo,

    /// <summary>程序化替换（例如绑定外部写入后的同步）。</summary>
    Programmatic,
}

/// <summary>事务触发原因；用于历史合并、诊断与取消策略判定，不改变替换语义。</summary>
public enum TextEditReason
{
    /// <summary>未指定。</summary>
    None = 0,

    /// <summary>用户输入（键盘/输入法）。</summary>
    UserInput,

    /// <summary>剪贴板操作。</summary>
    Clipboard,

    /// <summary>程序化写入。</summary>
    Programmatic,

    /// <summary>撤销/重做重放。</summary>
    History,

    /// <summary>绑定外部写入后的同步。</summary>
    BindingSync,
}

/// <summary>
/// 原子编辑操作：用 <see cref="Replacement"/> 的插入串替换 <see cref="Range"/> 范围内的已提交文本。
/// </summary>
/// <remarks>
/// <para>
/// 插入、删除、选区替换、剪切/粘贴、输入法结果、撤销重做共享同一形状，因此后续任务只需要一条替换通道。
/// 删除就是“空插入串的替换”；空范围替换就是插入。
/// </para>
/// <para>
/// TI-02 只冻结数据形状与校验规则；执行器由 TI-03 提供，并负责快照、写绑定一次、回读确认与版本推进。
/// </para>
/// </remarks>
public readonly record struct EditOperation
{
    private readonly string? _replacement;

    /// <summary>构造一个替换操作。</summary>
    /// <param name="range">被替换的 UTF-16 范围；落点是否在文档内由执行期校验。</param>
    /// <param name="replacement">插入串；null 视为空串。</param>
    /// <param name="kind">事务种类。</param>
    /// <param name="reason">触发原因。</param>
    /// <param name="scrubSingleLine">
    /// 为 true 时先按 <see cref="TextInputEvent.SanitizeSingleLine"/> 过滤 CR/LF/Tab 再使用插入串。
    /// 粘贴与宿主提交文本必须为 true（保持既有单行行为）；程序化写入可显式传 false。
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> 为 <see cref="TextEditKind.None"/>，或 <paramref name="scrubSingleLine"/> 为 true
    /// 时非空插入串全部被过滤掉（“粘贴净化后为空”与数据损坏无法区分）。调用方可用 <see cref="TryCreate"/>，
    /// 或改用 <see cref="Delete"/> 表达删除。
    /// </exception>
    public EditOperation(
        TextRange range,
        string? replacement,
        TextEditKind kind = TextEditKind.Typing,
        TextEditReason reason = TextEditReason.UserInput,
        bool scrubSingleLine = true)
    {
        if (!TryCreate(range, replacement, kind, reason, scrubSingleLine, out var operation, out var error))
        {
            throw new ArgumentException(error, nameof(replacement));
        }

        this = operation;
    }

    /// <summary>被替换的 UTF-16 范围。</summary>
    public TextRange Range { get; init; }

    /// <summary>插入串（已按 <see cref="Scrubbed"/> 决定过滤）；删除操作为空串；default 事件返回空串而不是 null。</summary>
    public string Replacement
    {
        get => _replacement ?? string.Empty;
        init => _replacement = value;
    }

    /// <summary>事务种类；未定义值会被 <see cref="TryCreate"/> 与构造函数拒绝。</summary>
    public TextEditKind Kind { get; init; }

    /// <summary>触发原因。</summary>
    public TextEditReason Reason { get; init; }

    /// <summary>插入串是否经过单行净化。</summary>
    public bool Scrubbed { get; init; }

    /// <summary>是否为删除（替换成空串且范围非空）。</summary>
    public bool IsDeletion => Range.Length > 0 && Replacement.Length == 0;

    /// <summary>是否为空操作（空范围 + 空插入串）；执行器不产生历史。</summary>
    public bool IsNoOp => Range.IsEmpty && Replacement.Length == 0;

    /// <summary>替换后文本长度的变化量（可正可负）。</summary>
    public int LengthDelta => Replacement.Length - Range.Length;

    /// <summary>
    /// 校验当前值是否仍满足事务规则；供 <c>init</c>/<c>with</c> 改写后的消费方（TI-03）使用。
    /// </summary>
    /// <remarks>
    /// 工厂与构造函数已做同样校验；但 <c>readonly record struct</c> 的公开 <c>init</c> 允许
    /// <c>with</c> 改写枚举值，因此 TI-03 在执行前必须再调用本方法，不能只依赖工厂“看起来有效”。
    /// 失败时返回 false 与诊断文本，不抛异常。
    /// </remarks>
    public bool TryValidate(out string? error) => TryValidateEnums(Kind, Reason, out error);

    /// <summary>严格校验并创建操作；失败时返回 false 与诊断文本，而不是抛异常。</summary>
    /// <remarks>
    /// 种类与原因必须是已定义的枚举值：<see cref="TextEditKind.None"/> 与任何未定义的整数值都被拒绝，
    /// 避免未知枚举被当作有效事务写入。
    /// </remarks>
    public static bool TryCreate(
        TextRange range,
        string? replacement,
        TextEditKind kind,
        TextEditReason reason,
        bool scrubSingleLine,
        out EditOperation operation,
        out string? error)
    {
        if (!TryValidateEnums(kind, reason, out error))
        {
            operation = default;
            return false;
        }

        var inserted = replacement ?? string.Empty;
        if (scrubSingleLine)
        {
            var scrubbed = TextInputEvent.SanitizeSingleLine(inserted);
            if (inserted.Length > 0 && scrubbed.Length == 0)
            {
                operation = default;
                error = "插入串经过单行净化后为空：这既可能是“粘贴净化后为空”，也可能是数据损坏，"
                    + "不能当成删除执行。请改用 EditOperation.Delete 表达删除，或检查来源文本。";
                return false;
            }

            inserted = scrubbed;
        }

        operation = Create(range, inserted, kind, reason, scrubSingleLine);
        error = null;
        return true;
    }

    /// <summary>
    /// 创建删除操作（空插入串）；与构造函数/TryCreate 使用同一套种类与原因校验。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> 为 <see cref="TextEditKind.None"/> 或未定义值，或 <paramref name="reason"/>
    /// 是未定义值时抛出（工厂不得绕过校验）。
    /// </exception>
    public static EditOperation Delete(TextRange range, TextEditKind kind = TextEditKind.Delete, TextEditReason reason = TextEditReason.UserInput)
    {
        if (!TryCreate(range, string.Empty, kind, reason, scrubSingleLine: false, out var operation, out var error))
        {
            throw new ArgumentException(error, nameof(kind));
        }

        return operation;
    }

    private static bool TryValidateEnums(TextEditKind kind, TextEditReason reason, out string? error)
    {
        if (kind == TextEditKind.None)
        {
            error = "编辑操作的种类不能是 TextEditKind.None；请显式指定 Typing/Paste/Cut/Delete/CompositionResult 等。";
            return false;
        }

        if (!Enum.IsDefined(kind))
        {
            error = $"编辑操作的种类 {(int)kind} 未定义；未知种类不得写入文档。";
            return false;
        }

        if (!Enum.IsDefined(reason))
        {
            error = $"编辑操作的触发原因 {(int)reason} 未定义；未知原因不得写入文档。";
            return false;
        }

        error = null;
        return true;
    }

    private static EditOperation Create(TextRange range, string replacement, TextEditKind kind, TextEditReason reason, bool scrubbed)
        => new()
        {
            Range = range,
            Replacement = replacement,
            Kind = kind,
            Reason = reason,
            Scrubbed = scrubbed,
        };

    /// <summary>只输出种类、范围与长度，不输出插入文本内容。</summary>
    public override string ToString()
        => $"{Kind}/{Reason} replace {Range} with len={Replacement.Length}{(Scrubbed ? " scrubbed" : string.Empty)}";
}

/// <summary>编辑结果状态：只描述“实际发生了什么”，不伪造成功。</summary>
public enum TextEditStatus
{
    /// <summary>未执行（默认）。</summary>
    None = 0,

    /// <summary>替换已接受，文档版本已推进。</summary>
    Accepted,

    /// <summary>被容量/净化规则整体拒绝；文档与版本保持不变。</summary>
    Rejected,

    /// <summary>回读值与操作前一致，未产生实际变化；不新增历史、不推进版本。</summary>
    Unchanged,
}

/// <summary>
/// 事务结果：操作前快照、操作后快照（从绑定回读确认的实际值）、状态与拒绝原因。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Before"/> 与 <see cref="After"/> 都是不可变快照。<see cref="AcceptedText"/> 是执行器写入绑定后
/// “回读确认”的实际文本，可能因为 setter 的转换/拒绝而不等于请求的替换结果；调用方必须使用回读值。
/// </para>
/// <para>
/// <b>不变量（构造期强制）：</b><see cref="DocumentVersion"/> 必须等于 <see cref="After"/> 的版本；
/// <see cref="AcceptedText"/> 必须等于 <see cref="After"/> 的文本；<see cref="TextEditStatus.Accepted"/>
/// 要求文本确实变化且 <see cref="After"/> 版本严格大于 <see cref="Before"/> 版本（一次成功事务通常 +1）；
/// <see cref="TextEditStatus.Unchanged"/> 与 <see cref="TextEditStatus.Rejected"/> 都要求文本与版本不变
/// （被拒绝的事务不改变文档；绑定转换后的真实重同步由 TI-03 另行定义，不得伪造“同版本不同文档”）。
/// </para>
/// <para>
/// <b>default 语义：</b><c>default(EditResult)</c> 的 <see cref="Status"/> 是
/// <see cref="TextEditStatus.None"/>，明确表示“无效/未执行”；<see cref="AcceptedText"/> 返回空串，
/// 所有查询对 default 安全。
/// </para>
/// </remarks>
public readonly record struct EditResult
{
    private readonly string? _acceptedText;

    /// <summary>构造事务结果并校验一致性。</summary>
    /// <param name="status">结果状态；必须是已定义的 <see cref="TextEditStatus"/> 且不能是 <see cref="TextEditStatus.None"/>。</param>
    /// <param name="before">操作前快照。</param>
    /// <param name="after">回读确认后的快照。</param>
    /// <param name="acceptedText">实际接受的文本（回读值）；null 视为空串。</param>
    /// <param name="documentVersion">结果对应的文档版本；必须等于 <paramref name="after"/> 的版本。</param>
    /// <param name="reason">非 Accepted 时的拒绝/未变化原因；Accepted 时必须是 null。</param>
    /// <exception cref="ArgumentException">状态未定义，或版本/文本不变量被破坏。</exception>
    public EditResult(
        TextEditStatus status,
        TextSnapshot before,
        TextSnapshot after,
        string? acceptedText,
        int documentVersion,
        string? reason = null)
    {
        if (status == TextEditStatus.None)
        {
            throw new ArgumentException("编辑结果的状态不能是 TextEditStatus.None。", nameof(status));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException($"编辑结果的状态 {(int)status} 未定义；未知状态不得被当作事务结论。", nameof(status));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(documentVersion);
        var text = acceptedText ?? string.Empty;

        if (documentVersion != after.Version)
        {
            throw new ArgumentException(
                $"结果版本必须等于 after 快照版本（documentVersion={documentVersion}, after.Version={after.Version}）；"
                + "两者不一致会产生冲突快照。",
                nameof(documentVersion));
        }

        if (!string.Equals(text, after.Text, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "结果的读回文本必须与 after 快照一致；请用回读值构造 after，不要使用自己重算的候选值。",
                nameof(after));
        }

        if (status == TextEditStatus.Accepted && string.Equals(before.Text, after.Text, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Accepted 结果必须伴随实际文本变化；文本未变时应报告 Unchanged。",
                nameof(after));
        }

        if (status == TextEditStatus.Accepted && after.Version <= before.Version)
        {
            throw new ArgumentException(
                $"Accepted 结果必须推进文档版本（before.Version={before.Version}, after.Version={after.Version}）；"
                + "版本未推进不能作为成功事务，否则历史与过时事件隔离都会失去依据。",
                nameof(after));
        }

        if (status == TextEditStatus.Unchanged
            && (!string.Equals(before.Text, after.Text, StringComparison.Ordinal) || before.Version != after.Version))
        {
            throw new ArgumentException(
                "Unchanged 结果的 before/after 必须文本与版本都一致；确实变化时应报告 Accepted。",
                nameof(after));
        }

        if (status == TextEditStatus.Rejected
            && (!string.Equals(before.Text, after.Text, StringComparison.Ordinal) || before.Version != after.Version))
        {
            throw new ArgumentException(
                "Rejected 结果不得改变文档：before/after 的文本与版本都必须一致。"
                + "绑定转换/规范化后的真实回读若与旧文本不同，应报告 Accepted 并留待 TI-03 明确重同步语义，"
                + "不能伪造“同一版本却换了文档”的 Rejected 结果。",
                nameof(after));
        }

        if (status == TextEditStatus.Accepted && reason is not null)
        {
            throw new ArgumentException("Accepted 结果不应携带拒绝原因。", nameof(reason));
        }

        Status = status;
        Before = before;
        After = after;
        _acceptedText = text;
        DocumentVersion = documentVersion;
        Reason = reason;
    }

    /// <summary>结果状态；<c>default</c> 为 <see cref="TextEditStatus.None"/>，表示无效/未执行。</summary>
    public TextEditStatus Status { get; }

    /// <summary>操作前快照。</summary>
    public TextSnapshot Before { get; }

    /// <summary>回读确认后的快照。</summary>
    public TextSnapshot After { get; }

    /// <summary>实际接受的文本（绑定回读值）；<c>default</c> 返回空串而不是 null。</summary>
    public string AcceptedText => _acceptedText ?? string.Empty;

    /// <summary>结果对应的文档版本（等于 <see cref="After"/> 的版本）。</summary>
    public int DocumentVersion { get; }

    /// <summary>非 Accepted 时的原因；Accepted 时为 null。</summary>
    public string? Reason { get; }

    /// <summary>操作是否实际改变了文档并推进版本。</summary>
    public bool Changed => Status == TextEditStatus.Accepted;

    /// <summary>未执行任何事务的结果（服务无能力/无焦点文本客户端时使用）。</summary>
    public static EditResult NotHandled { get; } = new(
        TextEditStatus.Rejected,
        TextSnapshot.Empty,
        TextSnapshot.Empty,
        string.Empty,
        0,
        "没有活动的文本输入客户端或服务不具备该能力；宿主应走已提交文本回退或忽略本次事件。");

    /// <summary>只输出状态、版本与长度，不输出用户文本。</summary>
    public override string ToString()
        => $"{Status} v{DocumentVersion} len {Before.Length}->{After.Length}"
            + (Reason is null ? string.Empty : $" ({Reason})");
}
