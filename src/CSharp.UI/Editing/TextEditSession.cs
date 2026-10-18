namespace CSharp.UI;

/// <summary>
/// 外部文本 / 绑定来源 / MaxLength 变化被观测后的同步结果（TI-03 内部）。
/// </summary>
/// <remarks>
/// 三个标志都是外部变化，需要推进文档版本、标记组合/历史失效接缝，并把选区限制到新文本。
/// 首次观测只建立基线，三个标志都为 false。
/// </remarks>
internal readonly record struct TextEditSyncResult(
    bool TextChanged,
    bool SourceChanged,
    bool MaxLengthChanged)
{
    /// <summary>是否检测到外部变化。</summary>
    public bool HasExternalChange => TextChanged || SourceChanged || MaxLengthChanged;
}

/// <summary>
/// 单行 TextField 的原子编辑会话（TI-03 内部实现）：确认文本、文档版本、绑定来源身份与原子替换事务。
/// </summary>
/// <remarks>
/// <para>
/// 所有已提交修改（普通键入、Backspace/Delete、剪切、粘贴、选区替换）都通过同一条
/// <see cref="Apply"/> 通道：先捕获操作前快照，算出候选文本，只写一次绑定，再回读实际值确认。
/// 选区移动与光标移动不经过本类型，因此不会制造文本事务。
/// </para>
/// <para>
/// <b>版本语义：</b><see cref="Version"/> 只在两种情况下推进——成功接受的事务（通常 +1），
/// 以及检测到外部文本 / 来源 / MaxLength 变化。绑定 setter 被拒绝、回读值与操作前完全一致时都不推进。
/// </para>
/// <para>
/// <b>失效接缝：</b>本阶段没有组合状态机（TI-04）也没有历史（TI-05），因此这里只把“需要失效”的事实
/// 记录成 <see cref="CompositionInvalidationRequested"/> 与 <see cref="HistoryInvalidationRequested"/>，
/// 不提前实现组合或撤销/重做。
/// </para>
/// <para>
/// <b>例外不伪造成功：</b>绑定 getter 抛错时不推进版本、不移动光标/选区；setter 抛错时先尝试回读实际文本
/// 并在确实发生了变化时重同步，然后原样重抛 setter 的异常（外部副作用不承诺回滚）。
/// </para>
/// </remarks>
internal sealed class TextEditSession
{
    private string _text = string.Empty;
    private int _version;
    private object? _identity;
    private bool _initialized;
    private int _maxLength = int.MaxValue;

    /// <summary>最近确认（自身事务回读或外部同步）的已提交文本。</summary>
    public string Text => _text;

    /// <summary>文档版本：成功事务或外部变化后推进，用于隔离过时事件。</summary>
    public int Version => _version;

    /// <summary>是否处于“无法可靠回读”的状态（例如 setter 抛错后 getter 也抛错），下一次成功读取时重同步。</summary>
    public bool RequiresResync { get; private set; }

    /// <summary>正在写入绑定的候选文本；写入期间的重建不能把它误判为外部变化（null 表示没有进行中的事务）。</summary>
    private string? PendingWrite { get; set; }

    /// <summary>组合状态需要失效（TI-04 消费；本阶段只记录）。</summary>
    public bool CompositionInvalidationRequested { get; private set; }

    /// <summary>编辑历史需要清空（TI-05 消费；本阶段只记录）。</summary>
    public bool HistoryInvalidationRequested { get; private set; }

    /// <summary>
    /// 把会话与一次真实回读对齐。第一次调用只初始化；之后任何来源 / 文本 / MaxLength 变化都会推进版本
    /// 并标记失效接缝。同值回读是真正的空操作，不会清空历史。
    /// </summary>
    public TextEditSyncResult Sync(object identity, string? text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var value = text ?? string.Empty;

        if (!_initialized)
        {
            _identity = identity;
            _text = value;
            _maxLength = maxLength;
            _version = 0;
            _initialized = true;
            PendingWrite = null;
            RequiresResync = false;
            return default;
        }

        var sourceChanged = !ReferenceEquals(_identity, identity);
        var textChanged = !string.Equals(_text, value, StringComparison.Ordinal);
        var maxLengthChanged = _maxLength != maxLength;

        if (sourceChanged || textChanged || maxLengthChanged)
        {
            _version++;
            RequestInvalidation();
        }

        _identity = identity;
        _text = value;
        _maxLength = maxLength;
        RequiresResync = false;
        return new TextEditSyncResult(textChanged, sourceChanged, maxLengthChanged);
    }

    /// <summary>当前回读是否正是本会话正在写入的候选文本（自己的事务触发的重建）。</summary>
    public bool IsOwnPendingWrite(string? text)
        => PendingWrite is not null && string.Equals(PendingWrite, text ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// 进行中的写入是否仍属于同一来源与同一 MaxLength。
    /// 同文本但已换来源 / 改变 MaxLength 的重建不能当成“自己的确认”，否则会串写新客户端。
    /// </summary>
    public bool MatchesContext(object identity, int maxLength)
        => ReferenceEquals(_identity, identity) && _maxLength == maxLength;

    /// <summary>
    /// 标记“无法可靠回读、下一次成功读取时重同步”，并保守标记组合/历史失效接缝。
    /// 只打标记、不抛次生异常，因此不会掩盖原始异常。
    /// </summary>
    public void MarkRequiresResync()
    {
        RequiresResync = true;
        RequestInvalidation();
    }

    /// <summary>
    /// 只消费“组合需要失效”的接缝位，保留历史失效位（TI-05 仍需看到需要清空历史的事实）。
    /// </summary>
    /// <returns>组合刷新/取消是否需要发生。</returns>
    public bool ConsumeCompositionInvalidation()
    {
        var value = CompositionInvalidationRequested;
        CompositionInvalidationRequested = false;
        return value;
    }

    /// <summary>取出并清空失效接缝（TI-04 / TI-05 接入后使用；本阶段供测试与后续消费）。</summary>
    public (bool Composition, bool History) ConsumeInvalidation()
    {
        var result = (CompositionInvalidationRequested, HistoryInvalidationRequested);
        CompositionInvalidationRequested = false;
        HistoryInvalidationRequested = false;
        return result;
    }

    /// <summary>
    /// 执行一次原子替换事务：读取实际文本 → 同步外部变化 → 计算候选（容量 / 整字素截断）→ 只写一次绑定 → 回读确认。
    /// </summary>
    /// <param name="identity">当前绑定的 <see cref="Binding{T}.Identity"/>，用于识别来源替换。</param>
    /// <param name="maxLength">当前生效的 UTF-16 MaxLength（<see cref="int.MaxValue"/> 表示不限制）。</param>
    /// <param name="operation">要执行的替换操作；执行前会再调用 <see cref="EditOperation.TryValidate"/>。</param>
    /// <param name="selection">操作前的运行时选区（保留方向）。</param>
    /// <param name="read">读取绑定实际值；抛错时必须原样传播且不伪造成功。</param>
    /// <param name="write">写入绑定实际值；本方法保证成功路径上只调用一次。</param>
    public EditResult Apply(
        object identity,
        int maxLength,
        EditOperation operation,
        TextSelection selection,
        Func<string?> read,
        Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        // 第一步：读取真实文本。getter 抛错时不改版本、不移动光标/选区，异常原样上抛。
        var beforeText = read() ?? string.Empty;

        // 第二步：与真实回读对齐。外部写入 / 来源替换 / MaxLength 变化在这里推进版本并标记失效接缝。
        Sync(identity, beforeText, maxLength);

        var beforeSelection = SnapSelection(beforeText, selection);
        var before = new TextSnapshot(beforeText, beforeSelection, _version);

        if (!operation.TryValidate(out var validationError))
        {
            return Reject(before, validationError ?? "编辑操作未通过校验。");
        }

        var range = operation.Range;
        if (!range.IsWithin(beforeText.Length)
            || !TextGraphemeBoundaries.AllAreBoundaries(beforeText, [range.Start, range.End], out _))
        {
            // 无效 / 越界 / 落在代理对、组合附加符或 ZWJ 序列内部的范围不得拆字写入。
            return Reject(before, "替换范围越界或落在字素内部：不得拆字写入。");
        }

        // 容量按替换后释放的空间计算（选区释放的容量计入），并只在完整字素边界上截断插入内容。
        var inserted = operation.Replacement;
        var capacity = Math.Max(0, maxLength - (beforeText.Length - range.Length));
        var fitted = FitWholeGraphemes(inserted, capacity);
        if (inserted.Length > 0 && fitted.Length == 0)
        {
            return Reject(before, "剩余 UTF-16 容量放不下任何完整字素：整体拒绝，不写绑定、不移动选区。");
        }

        var candidate = string.Concat(beforeText.AsSpan(0, range.Start), fitted, beforeText.AsSpan(range.End));
        if (string.Equals(candidate, beforeText, StringComparison.Ordinal))
        {
            // 删除空范围等真正的空操作：不调用 setter。
            return UnchangedResult(before, "候选文本与当前文档一致；未调用绑定 setter。");
        }

        var targetCaret = range.Start + fitted.Length;

        // 第三步：只写一次绑定。
        PendingWrite = candidate;
        try
        {
            write(candidate);
        }
        catch
        {
            PendingWrite = null;

            // setter 抛错：写入结果不可信，组合/历史保守失效；能回读就按实际值重同步，不能则标记待重同步。
            RequestInvalidation();
            ReadBackAfterFailedWrite(beforeText, read);
            throw;
        }

        PendingWrite = null;

        // 第四步：回读确认。getter 抛错时保留 RequiresResync 并上抛，绝不把候选当成已接受。
        string actual;
        try
        {
            actual = read() ?? string.Empty;
        }
        catch
        {
            MarkRequiresResync();
            throw;
        }

        if (string.Equals(actual, beforeText, StringComparison.Ordinal))
        {
            // setter 拒绝写入或把值规范化回旧值：文档实际没有变化，不能按接受处理。
            return Reject(before, "绑定 setter 未改变文档（拒绝写入或规范化回旧值）。");
        }

        // 回读值与旧文本不同 → 必须报告 Accepted 并推进版本（不得构造“同版本却换了文档”的结果）。
        var normalized = !string.Equals(actual, candidate, StringComparison.Ordinal);

        // 确认后的选区必须落在字素边界上，否则返回的 EditResult 快照会与真实文档不一致；
        // 规范化后的文本结构可能与候选不同，因此按实际回读文本重新吸附（优先向后越过完整字素）。
        var caret = SnapToGraphemeBoundary(actual, Math.Clamp(targetCaret, 0, actual.Length), roundUp: true);
        var afterSelection = TextSelection.Caret(caret);
        _version++;
        _text = actual;
        _identity = identity;
        _maxLength = maxLength;
        RequiresResync = false;

        if (normalized)
        {
            // 绑定转换/规范化：实际同步结果与请求候选不同，不能为“请求的替换”记录逆操作，
            // 因此标记组合/历史失效接缝（真正执行属 TI-04 / TI-05）。
            RequestInvalidation();
        }

        var after = new TextSnapshot(actual, afterSelection, _version);
        return new EditResult(TextEditStatus.Accepted, before, after, actual, after.Version);
    }

    /// <summary>setter 抛错后尝试回读：确实变了就重同步（不伪造回滚），读不到就标记待重同步。</summary>
    private void ReadBackAfterFailedWrite(string beforeText, Func<string?> read)
    {
        try
        {
            var actual = read() ?? string.Empty;
            if (!string.Equals(actual, beforeText, StringComparison.Ordinal))
            {
                _text = actual;
                _version++;
                RequestInvalidation();
            }

            RequiresResync = false;
        }
        catch
        {
            // 无法回读：外部副作用已经发生但无法确认，标记待重同步并保守失效组合/历史。
            MarkRequiresResync();
        }
    }

    private static EditResult Reject(TextSnapshot before, string reason)
        => new(TextEditStatus.Rejected, before, before, before.Text, before.Version, reason);

    private static EditResult UnchangedResult(TextSnapshot before, string reason)
        => new(TextEditStatus.Unchanged, before, before, before.Text, before.Version, reason);

    private void RequestInvalidation()
    {
        CompositionInvalidationRequested = true;
        HistoryInvalidationRequested = true;
    }

    /// <summary>把选区端点吸附到真实文本的字素边界（核心 StringInfo，不要求排版器）。</summary>
    private static TextSelection SnapSelection(string text, TextSelection selection)
    {
        var clamped = selection.Clamp(text.Length);
        if (clamped.IsCollapsed)
        {
            return TextSelection.Caret(SnapToGraphemeBoundary(text, clamped.Active));
        }

        // 起点向下、活动端按方向吸附：折叠到同一字素时退化为光标，绝不返回字素内部位置。
        var anchor = SnapToGraphemeBoundary(text, clamped.Anchor);
        var active = SnapToGraphemeBoundary(text, clamped.Active, clamped.Active > clamped.Anchor);
        return new TextSelection(anchor, active);
    }

    /// <summary>把任意 UTF-16 偏移吸附到最近的字素边界（<paramref name="roundUp"/> 为 true 时向后取）。</summary>
    private static int SnapToGraphemeBoundary(string text, int offset, bool roundUp = false)
    {
        if (offset <= 0)
        {
            return 0;
        }

        if (offset >= text.Length)
        {
            return text.Length;
        }

        var boundaries = new List<int> { 0 };
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
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

    /// <summary>
    /// 按字素边界把插入内容截断到 <paramref name="capacity"/> 个 UTF-16 码元之内。
    /// 无法完整放入的字素会被整个拒绝，避免拆开代理对或组合字符。
    /// </summary>
    internal static string FitWholeGraphemes(string inserted, int capacity)
    {
        if (inserted.Length == 0 || capacity <= 0)
        {
            return string.Empty;
        }

        if (inserted.Length <= capacity)
        {
            return inserted;
        }

        var best = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(inserted);
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (next > capacity)
            {
                break;
            }

            best = next;
        }

        return best == 0 ? string.Empty : inserted[..best];
    }
}
