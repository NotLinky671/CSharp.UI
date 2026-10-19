namespace CSharp.UI;

/// <summary>
/// 平台编辑快捷键策略（TI-05 核心内部）：只用于解析“本平台”的 Undo/Redo/Cut/Copy/Paste/SelectAll，
/// 不代表任何原生消息桥已经接通。宿主最终路由属 TI-07。
/// </summary>
internal enum TextEditPlatformKind
{
    /// <summary>Windows：Control 为主修饰键。</summary>
    Windows,

    /// <summary>Linux：Control 为主修饰键。</summary>
    Linux,

    /// <summary>macOS：Command（Meta）为主修饰键；Control 不触发编辑命令。</summary>
    MacOS,
}

/// <summary>统一内部编辑命令（TI-05）：由 <see cref="TextEditCommandResolver"/> 解析，不扩张公共编辑器 API。</summary>
internal enum TextEditCommand
{
    /// <summary>撤销上一项历史事务。</summary>
    Undo,

    /// <summary>重做被撤销的事务。</summary>
    Redo,

    /// <summary>剪切已提交选区。</summary>
    Cut,

    /// <summary>复制已提交选区（不取消预编辑）。</summary>
    Copy,

    /// <summary>粘贴剪贴板文本。</summary>
    Paste,

    /// <summary>全选已提交文档。</summary>
    SelectAll,
}

/// <summary>
/// 平台快捷键解析（TI-05 内部，可注入平台以便纯核心测试）。
/// </summary>
/// <remarks>
/// <para>
/// 规则（与计划§9/任务修订一致）：Windows/Linux 为 Ctrl+Z、Ctrl+Y、Ctrl+Shift+Z；macOS 为 Command+Z、
/// Command+Shift+Z（Command+Y 不重做）。错误平台的主修饰键、Alt/AltGr（Ctrl+Alt）以及多余修饰键都不执行命令；
/// CapsLock 不改变含义。这里<b>不</b>使用 <see cref="KeyEvent.IsCommandPressed"/> 的 Control∪Meta 并集逻辑，
/// 因为那会把 Mac 的 Control 或 Windows 的 Win 键误当成命令键。
/// </para>
/// <para>
/// 解析结果只描述“本平台应执行哪个命令”；输入法是否已经消费本次原生按键由运行时/控件在调用前单独判定。
/// </para>
/// </remarks>
internal static class TextEditCommandResolver
{
    /// <summary>当前运行平台的默认策略（真机 mac/Linux 未在本任务验收，仅用于默认映射）。</summary>
    internal static TextEditPlatformKind DefaultPlatform
        => OperatingSystem.IsMacOS()
            ? TextEditPlatformKind.MacOS
            : OperatingSystem.IsWindows()
                ? TextEditPlatformKind.Windows
                : TextEditPlatformKind.Linux;

    /// <summary>
    /// 解析一次按键事件；返回 null 表示“本平台不把该组合当作编辑命令”。
    /// </summary>
    internal static TextEditCommand? Resolve(KeyEvent e, TextEditPlatformKind platform)
    {
        // CapsLock 与命令含义无关：先剔除，再判断平台主修饰键与额外修饰键。
        var modifiers = e.Modifiers & ~KeyModifiers.CapsLock;
        var primary = platform == TextEditPlatformKind.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        var foreignPrimary = platform == TextEditPlatformKind.MacOS ? KeyModifiers.Control : KeyModifiers.Meta;

        if ((modifiers & primary) == 0)
        {
            return null;
        }

        // 错误平台的主修饰键不得触发命令（例如 Windows 的 Win+Z、macOS 的 Control+Z）。
        if ((modifiers & foreignPrimary) != 0)
        {
            return null;
        }

        // Alt / AltGr（在 Windows 上常表现为 Control+Alt）不是编辑命令。
        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            return null;
        }

        var shift = (modifiers & KeyModifiers.Shift) != 0;
        if ((modifiers & ~(primary | KeyModifiers.Shift)) != KeyModifiers.None)
        {
            // 不支持的额外修饰键组合不执行命令。
            return null;
        }

        return e.Key switch
        {
            UiKey.Z => shift ? TextEditCommand.Redo : TextEditCommand.Undo,
            UiKey.Y => platform != TextEditPlatformKind.MacOS && !shift ? TextEditCommand.Redo : null,
            UiKey.X => !shift ? TextEditCommand.Cut : null,
            UiKey.C => !shift ? TextEditCommand.Copy : null,
            UiKey.V => !shift ? TextEditCommand.Paste : null,
            UiKey.A => !shift ? TextEditCommand.SelectAll : null,
            _ => null,
        };
    }
}

/// <summary>
/// 每个运行时节点的历史容量配置（TI-05 内部）：undo + redo 合计的项数与保留文本 UTF-16 总量。
/// </summary>
/// <remarks>
/// 0 容量禁用记录（编辑仍然可用）。默认 100 项 / 1,048,576 个 UTF-16 码元；测试可用更小容量。
/// </remarks>
internal readonly record struct TextHistoryCapacity(int MaxEntries, long MaxTextLength)
{
    /// <summary>计划默认上限。</summary>
    public static TextHistoryCapacity Default { get; } = new(100, 1_048_576);

    /// <summary>禁用历史记录（不限制编辑）。</summary>
    public static TextHistoryCapacity Disabled { get; } = new(0, 0);

    /// <summary>是否启用记录；任一项为 0 即禁用。</summary>
    public bool IsEnabled => MaxEntries > 0 && MaxTextLength > 0;
}

/// <summary>一条历史事务的可合并类别（只允许同类别、连续范围的相邻编辑合并）。</summary>
internal enum TextEditMergeKind
{
    /// <summary>不参与合并（选区替换、剪贴板、组合结果、程序化写入等）。</summary>
    None = 0,

    /// <summary>折叠光标处的相邻普通键入。</summary>
    TypingAppend,

    /// <summary>同方向（向后）的逐字素退格删除。</summary>
    BackspaceDelete,

    /// <summary>同方向（向前）的逐字素 Delete 删除。</summary>
    ForwardDelete,
}

/// <summary>
/// 历史事务的增量记录（TI-05）：只保存替换起点、实际删除/插入片段、前后方向选区与分组信息，
/// <b>不</b>保存整份文档快照，因此容量预算有意义。
/// </summary>
internal sealed class TextHistoryEntry
{
    internal TextHistoryEntry(
        int replacementStart,
        string deleted,
        string inserted,
        TextSelection selectionBefore,
        TextSelection selectionAfter,
        TextEditKind kind,
        TextEditMergeKind mergeKind)
    {
        ReplacementStart = replacementStart;
        Deleted = deleted;
        Inserted = inserted;
        SelectionBefore = selectionBefore;
        SelectionAfter = selectionAfter;
        Kind = kind;
        MergeKind = mergeKind;
    }

    /// <summary>替换起点（相对于该事务执行前的文档坐标）。</summary>
    internal int ReplacementStart { get; set; }

    /// <summary>实际删除的文本片段。</summary>
    internal string Deleted { get; set; }

    /// <summary>实际插入的文本片段。</summary>
    internal string Inserted { get; set; }

    /// <summary>执行前的方向选区（撤销时恢复）。</summary>
    internal TextSelection SelectionBefore { get; }

    /// <summary>执行后的方向选区（重做时恢复）。</summary>
    internal TextSelection SelectionAfter { get; set; }

    /// <summary>事务种类（诊断/分组）。</summary>
    internal TextEditKind Kind { get; }

    /// <summary>可合并类别。</summary>
    internal TextEditMergeKind MergeKind { get; }

    /// <summary>该事务纳入预算的 UTF-16 长度（删除片段 + 插入片段）。</summary>
    internal long FragmentLength => (long)Deleted.Length + Inserted.Length;
}

/// <summary>
/// 每节点有界撤销/重做历史（TI-05）：增量片段、双上限、整事务淘汰、500ms 单调时钟合并与逆操作重放数据。
/// </summary>
/// <remarks>
/// <para>
/// <b>预算：</b>undo 与 redo 合计最多 <see cref="MaxEntries"/> 项，保留的删除/插入片段 UTF-16 长度之和最多
/// <see cref="MaxTextLength"/>。项在栈之间移动只转移同一个实例，计数不重复。单次事务片段总量超过总预算时仍由调用方
/// 执行编辑，但历史被清空且不保存该项。计数使用 <see cref="long"/>，避免整数溢出。
/// </para>
/// <para>
/// <b>合并：</b>只有同一节点、同一合并类别（折叠光标相邻键入或同方向逐字素删除）、范围连续且相邻两次成功编辑的
/// 单调时间间隔在 0～500ms（含边界）内才合并。合并后的条目仍是一笔完整事务，不保留部分可撤销字符；合并会超出
/// 文本预算时停止合并，按完整事务入栈后整事务淘汰。
/// </para>
/// <para>
/// <b>边界：</b>本类型只做核心内存操作，不读绑定、不排版、不调用任何平台服务；何时打组/清理由 TextField 决定。
/// </para>
/// </remarks>
internal sealed class TextEditHistory
{
    /// <summary>相邻成功编辑允许合并的最大单调时间间隔（含边界）。</summary>
    internal static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(500);

    private readonly List<TextHistoryEntry> _undo = [];
    private readonly List<TextHistoryEntry> _redo = [];
    private long _textLength;
    private TimeSpan? _lastAcceptedAt;

    internal TextEditHistory(TextHistoryCapacity capacity)
    {
        MaxEntries = Math.Max(0, capacity.MaxEntries);
        MaxTextLength = Math.Max(0, capacity.MaxTextLength);
    }

    /// <summary>undo + redo 合计最大项数；0 表示禁用。</summary>
    internal int MaxEntries { get; }

    /// <summary>undo + redo 合计保留文本的最大 UTF-16 长度；0 表示禁用。</summary>
    internal long MaxTextLength { get; }

    /// <summary>是否启用记录。</summary>
    internal bool IsEnabled => MaxEntries > 0 && MaxTextLength > 0;

    /// <summary>undo 项数。</summary>
    internal int UndoCount => _undo.Count;

    /// <summary>redo 项数。</summary>
    internal int RedoCount => _redo.Count;

    /// <summary>undo + redo 保留文本片段的 UTF-16 总长度（用于预算断言）。</summary>
    internal long RetainedTextLength => _textLength;

    /// <summary>是否存在可撤销事务。</summary>
    internal bool CanUndo => _undo.Count > 0;

    /// <summary>是否存在可重做事务。</summary>
    internal bool CanRedo => _redo.Count > 0;

    /// <summary>下一次撤销会重放的事务；无可用项时为 null。</summary>
    internal TextHistoryEntry? UndoPeek => _undo.Count > 0 ? _undo[^1] : null;

    /// <summary>下一次重做会重放的事务；无可用项时为 null。</summary>
    internal TextHistoryEntry? RedoPeek => _redo.Count > 0 ? _redo[^1] : null;

    /// <summary>打断合并（光标/选区操作、焦点/窗口变化、剪贴板命令、组合阶段、Undo/Redo 等）。</summary>
    internal void BreakMerge() => _lastAcceptedAt = null;

    /// <summary>清空两栈与计数（外部变化、不可信事务、超大项）。</summary>
    internal void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _textLength = 0;
        _lastAcceptedAt = null;
    }

    /// <summary>
    /// 记录一次<b>已确认</b>的普通编辑事务；只有在真实事务精确落地后才调用。
    /// </summary>
    /// <param name="entry">增量事务。</param>
    /// <param name="now">单调注入时钟的当前时间（合并窗口唯一依据）。</param>
    internal void Record(TextHistoryEntry entry, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsEnabled)
        {
            // 0 容量只禁用记录，不禁用编辑。
            _lastAcceptedAt = now;
            return;
        }

        // 实际接受的新编辑清空 redo（不会重复计数：被丢弃的项先扣除预算）。
        ClearRedo();

        // 单次事务片段总量超过总预算：仍执行编辑，但清空历史且不保存该项。
        if (entry.FragmentLength > MaxTextLength)
        {
            Clear();
            _lastAcceptedAt = now;
            return;
        }

        if (TryMergeIntoNewest(entry, now))
        {
            return;
        }

        _undo.Add(entry);
        _textLength += entry.FragmentLength;
        EvictOldestWholeTransactions();
        _lastAcceptedAt = now;
    }

    /// <summary>撤销栈顶事务后把它移到 redo（同一实例，不重复计数）。</summary>
    internal void CommitUndo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
    }

    /// <summary>重做 redo 栈顶事务后把它移回 undo（同一实例，不重复计数）。</summary>
    internal void CommitRedo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
    }

    private bool TryMergeIntoNewest(TextHistoryEntry entry, TimeSpan now)
    {
        if (_lastAcceptedAt is not { } last || _undo.Count == 0)
        {
            return false;
        }

        var elapsed = now - last;
        if (elapsed < TimeSpan.Zero || elapsed > MergeWindow)
        {
            // 负间隔或超过窗口不合并；窗口为闭区间（恰好 500ms 仍合并）。
            return false;
        }

        var newest = _undo[^1];
        if (newest.MergeKind == TextEditMergeKind.None || newest.MergeKind != entry.MergeKind)
        {
            return false;
        }

        // 合并会超出文本预算时停止合并：按完整事务入栈/淘汰，绝不留下部分可撤销字符。
        if (newest.FragmentLength + entry.FragmentLength > MaxTextLength)
        {
            return false;
        }

        if (!IsAdjacent(newest, entry))
        {
            return false;
        }

        switch (entry.MergeKind)
        {
            case TextEditMergeKind.TypingAppend:
                newest.Inserted += entry.Inserted;
                break;
            case TextEditMergeKind.BackspaceDelete:
                newest.Deleted = entry.Deleted + newest.Deleted;
                newest.ReplacementStart = entry.ReplacementStart;
                break;
            case TextEditMergeKind.ForwardDelete:
                newest.Deleted += entry.Deleted;
                break;
            default:
                return false;
        }

        newest.SelectionAfter = entry.SelectionAfter;
        _textLength += entry.FragmentLength;

        // 合并后的整事务本身已确认不超过文本预算；合并仍可能让总保留量超过预算，
        // 因此继续按“最老完整事务”淘汰（绝不拆分合并后的条目）。
        EvictOldestWholeTransactions();
        _lastAcceptedAt = now;
        return true;
    }

    /// <summary>检查两条事务是否在文档坐标上真正连续（同类别、折叠光标语义由 MergeKind 保证）。</summary>
    private static bool IsAdjacent(TextHistoryEntry older, TextHistoryEntry newer) => newer.MergeKind switch
    {
        TextEditMergeKind.TypingAppend =>
            older.Inserted.Length > 0
            && newer.Inserted.Length > 0
            && older.Deleted.Length == 0
            && newer.Deleted.Length == 0
            && newer.ReplacementStart == older.ReplacementStart + older.Inserted.Length,
        TextEditMergeKind.BackspaceDelete =>
            older.Inserted.Length == 0
            && newer.Inserted.Length == 0
            && newer.ReplacementStart + newer.Deleted.Length == older.ReplacementStart,
        TextEditMergeKind.ForwardDelete =>
            older.Inserted.Length == 0
            && newer.Inserted.Length == 0
            && newer.ReplacementStart == older.ReplacementStart,
        _ => false,
    };

    private void ClearRedo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        foreach (var entry in _redo)
        {
            _textLength -= entry.FragmentLength;
        }

        _redo.Clear();
    }

    /// <summary>从最老完整事务开始淘汰，直到满足项数与文本预算（计数为 O(1)，项数有界）。</summary>
    private void EvictOldestWholeTransactions()
    {
        while (_undo.Count + _redo.Count > MaxEntries || _textLength > MaxTextLength)
        {
            if (_undo.Count > 0)
            {
                var oldest = _undo[0];
                _undo.RemoveAt(0);
                _textLength -= oldest.FragmentLength;
                continue;
            }

            if (_redo.Count > 0)
            {
                var oldest = _redo[0];
                _redo.RemoveAt(0);
                _textLength -= oldest.FragmentLength;
                continue;
            }

            break;
        }
    }
}
