namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>ACP 文本写入来源：区分应用侧外部修改与 TSF 锁会话内的写入。</summary>
internal enum AcpEditOrigin
{
    /// <summary>应用自身（普通字符、退格、点击定位等权威修改）。</summary>
    App,

    /// <summary>TSF/输入法在写锁会话内写入（预编辑或结果）——<b>暂存</b>，不直接成为权威。</summary>
    Tsf,
}

/// <summary>一次 ACP 文本变更（UTF-16 码元偏移）。</summary>
internal readonly record struct AcpTextChange(int Start, int OldEnd, int NewEnd, long Version)
{
    internal int Delta => NewEnd - OldEnd;

    internal int InsertedLength => NewEnd - Start;
}

/// <summary>
/// ACP 文档模型（TI-07 P2b）：UTF-16 码元偏移、文档版本、单一选区。
/// </summary>
/// <remarks>
/// <para>
/// <b>码元而非字素：</b>ACP 偏移是 UTF-16 码元偏移。TSF 暂存写入允许在代理对中间切分
/// （<c>cch</c> 由原生方给出），本类按码元原样读写，不做字素吸附；<b>最终权威提交</b>仍由核心
/// <see cref="TextFieldComposition"/> 的字素规则裁决。
/// </para>
/// <para>纯托管、无 COM/窗口依赖：协议测试可直接驱动，不依赖真实 TSF 会话。</para>
/// </remarks>
internal sealed class AcpTextDocument
{
    private readonly List<char> _text = [];
    private int _selectionStart;
    private int _selectionEnd;

    /// <summary>活动端在锚点之前（反向选择）。ACP 用 <c>TS_AE_START</c> 表达。</summary>
    private bool _selectionBackward;

    /// <summary>单调文档版本：每次真实文本写入递增（选区变化不推进）。</summary>
    internal long Version { get; private set; }

    /// <summary>UTF-16 码元长度。</summary>
    internal int Length => _text.Count;

    /// <summary>当前文本快照。</summary>
    internal string Text => new(_text.ToArray());

    /// <summary>当前单选区（ACP 规范化的 <c>start &lt;= end</c> 偏移）。</summary>
    internal (int Start, int End) Selection => (_selectionStart, _selectionEnd);

    /// <summary>
    /// 当前单选区，<b>保留真实方向</b>（锚点/活动端）。
    /// </summary>
    /// <remarks>
    /// <see cref="Selection"/> 只给规范化端点，无法区分“caret 在 1”与“反向选中 3→1”。
    /// 原生事实必须冻结有向选区，否则完成事务只能看到一个排序后的范围而丢掉方向。
    /// </remarks>
    internal TextSelection DirectedSelection => _selectionBackward
        ? new TextSelection(_selectionEnd, _selectionStart)
        : new TextSelection(_selectionStart, _selectionEnd);

    /// <summary>选区是否反向（活动端在锚点之前）。</summary>
    internal bool IsSelectionBackward => _selectionBackward;

    /// <summary>按 <see cref="TextSelection"/> 的锚点/活动端设置选区（自动规范化并记录方向）。</summary>
    internal bool TrySetSelection(TextSelection selection)
    {
        var start = Math.Min(selection.Anchor, selection.Active);
        var end = Math.Max(selection.Anchor, selection.Active);
        if (!IsValidRange(start, end))
        {
            return false;
        }

        var backward = selection.Active < selection.Anchor;
        if (start == _selectionStart && end == _selectionEnd && backward == _selectionBackward)
        {
            return true;
        }

        _selectionStart = start;
        _selectionEnd = end;
        _selectionBackward = backward;
        SelectionChanged?.Invoke();
        return true;
    }

    /// <summary>文本写入后触发（在调用方持写锁期间）。</summary>
    internal event Action<AcpTextChange, AcpEditOrigin>? Changed;

    /// <summary>选区真实变化后触发。</summary>
    internal event Action? SelectionChanged;

    /// <summary>重置文档内容（应用侧外部更新；选区归零）。</summary>
    internal void Reset(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text.Clear();
        _text.AddRange(text);
        _selectionStart = 0;
        _selectionEnd = 0;
        _selectionBackward = false;
        Version++;
    }

    /// <summary>范围是否合法（含空范围与端点等于长度）。</summary>
    internal bool IsValidRange(int start, int end)
        => start >= 0 && end >= start && end <= _text.Count;

    /// <summary>读取范围文本；范围非法时抛出（调用方必须先校验）。</summary>
    internal string GetText(int start, int end)
    {
        if (!IsValidRange(start, end))
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"ACP range [{start},{end}) is outside [0,{_text.Count}].");
        }

        return new string(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_text)[start..end]);
    }

    /// <summary>
    /// 替换 <c>[start,end)</c> 为 <paramref name="text"/>；范围非法返回 false（调用方映射为 TS_E_INVALIDPOS）。
    /// </summary>
    internal bool TryReplace(int start, int end, string text, AcpEditOrigin origin, out AcpTextChange change)
    {
        change = default;
        if (!IsValidRange(start, end) || text is null)
        {
            return false;
        }

        _text.RemoveRange(start, end - start);
        _text.InsertRange(start, text);
        Version++;

        _selectionStart = MapPoint(_selectionStart, start, end, text.Length);
        _selectionEnd = MapPoint(_selectionEnd, start, end, text.Length);

        change = new AcpTextChange(start, end, start + text.Length, Version);
        Changed?.Invoke(change, origin);
        return true;
    }

    /// <summary>按已规范化的 <c>[start,end)</c> 设置选区（正向）。范围非法返回 false。</summary>
    internal bool TrySetSelection(int start, int end)
        => TrySetSelection(new TextSelection(start, end));

    /// <summary>把点按“插入/删除”语义映射到替换后的位置。</summary>
    private static int MapPoint(int point, int start, int oldEnd, int insertedLength)
    {
        if (point <= start)
        {
            return point;
        }

        if (point >= oldEnd)
        {
            return point + insertedLength - (oldEnd - start);
        }

        return start + insertedLength;
    }
}
