namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>一次暂存编辑的语义类别（由原生写入标志推导，不做内容猜测）。</summary>
internal enum AcpStagedEditKind
{
    /// <summary>TSF 在写锁内插入/替换文本（预编辑或结果候选）。</summary>
    Insert = 0,

    /// <summary>TSF 在写锁内删除文本。</summary>
    Delete = 1,
}

/// <summary>
/// 一次<b>暂存</b>的原生编辑批次（不可变）：TSF 写锁会话内的写入快照。
/// </summary>
/// <remarks>
/// <para>
/// <b>暂存 vs 权威：</b>本类型只描述“原生侧刚做了什么”，<b>不是</b>
/// <see cref="TextInputAuthoritativeSnapshot"/>，也绝不代表已提交文本。它不含也不推断
/// 结果/取消语义——那是下一批 owner 观察器的职责。
/// </para>
/// <para>
/// <b>偏移：</b>全部是 UTF-16 码元偏移（ACP 坐标系），与 <see cref="AcpTextDocument"/> 一致；
/// 允许在代理对中间切分。
/// </para>
/// <para><b>不可变：</b>构造后只读；<see cref="Inserted"/> 为冻结字符串。</para>
/// <para><b>诊断不含用户原文：</b><see cref="ToString"/> 只输出偏移与长度。</para>
/// </remarks>
internal sealed class AcpStagedEdit
{
    private readonly string _inserted;

    internal AcpStagedEdit(
        AcpStagedEditKind kind,
        AcpTextChange change,
        string? inserted,
        bool correction,
        long nativeGeneration)
    {
        Kind = kind;
        Change = change;
        _inserted = inserted ?? string.Empty;
        IsCorrection = correction;
        NativeGeneration = nativeGeneration;
    }

    /// <summary>编辑类别。</summary>
    public AcpStagedEditKind Kind { get; }

    /// <summary>真实变更范围（ACP 偏移 + 变更后的文档版本）。</summary>
    public AcpTextChange Change { get; }

    /// <summary>本次插入的文本（删除时为空串）。</summary>
    public string Inserted => _inserted;

    /// <summary>原生是否以 <c>TS_ST_CORRECTION</c> 标记本次写入（应用侧纠正）。</summary>
    public bool IsCorrection { get; }

    /// <summary>产生该编辑的原生代数（用于隔离陈旧批次）。</summary>
    public long NativeGeneration { get; }

    /// <summary>本次插入的 UTF-16 码元数。</summary>
    public int InsertedLength => _inserted.Length;

    /// <summary>只输出偏移与长度，不输出用户原文。</summary>
    public override string ToString()
        => $"{Kind} [{Change.Start},{Change.OldEnd})->{Change.NewEnd} "
            + $"insertedLen={_inserted.Length} v{Change.Version} gen={NativeGeneration}"
            + (IsCorrection ? " correction" : string.Empty);
}

/// <summary>
/// 生产暂存缓冲区（TI-07 P2b）：收集 TSF 写锁批次内的原生编辑，供下一批 owner 观察器消费。
/// </summary>
/// <remarks>
/// <para>
/// <b>核心边界（本批最重要的一条）：</b>暂存编辑<b>绝不</b>直接写 Binding、<b>绝不</b>推断结果或取消、
/// <b>绝不</b>成为权威文档。它们只是“原生侧发生了什么的不可变记录”。
/// </para>
/// <para>
/// <b>不竞争权威编辑：</b>本类型不持有 <see cref="ITextInputClient"/>，也没有任何写绑定路径。
/// 权威文档只由 <see cref="TextFieldComposition"/> 的真实事务写入。
/// </para>
/// <para>
/// <b>有界：</b>批次最多保留 <see cref="Capacity"/> 条，超出时丢弃最旧的并计数
/// <see cref="DroppedCount"/>——绝不无界增长，也不静默丢最新事实。
/// </para>
/// <para>
/// <b>代数隔离：</b>每条编辑带 <see cref="NativeGeneration"/>；<see cref="DrainForGeneration"/> 只返回
/// 当前代数的编辑，陈旧代数被丢弃，防止旧会话批次被误当成新会话的输入。
/// </para>
/// </remarks>
internal sealed class AcpStagingBuffer
{
    /// <summary>默认保留的暂存编辑上限。</summary>
    internal const int DefaultCapacity = 64;

    private readonly object _gate = new();
    private readonly List<AcpStagedEdit> _pending = [];
    private readonly int _capacity;

    private long _generation;
    private int _dropped;

    internal AcpStagingBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>当前原生代数；每次会话重置/失效递增。</summary>
    internal long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>当前未消费的暂存编辑数量。</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>因超出容量被丢弃的编辑总数。</summary>
    internal int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>推进代数并使已有暂存失效（换会话/失效/释放时调用）。</summary>
    internal void AdvanceGeneration()
    {
        lock (_gate)
        {
            _generation++;
            _pending.Clear();
        }
    }

    /// <summary>记录一条暂存编辑（由 store 在真实写入成功后调用）。</summary>
    internal AcpStagedEdit Record(AcpStagedEditKind kind, AcpTextChange change, string? inserted, bool correction)
    {
        lock (_gate)
        {
            var edit = new AcpStagedEdit(kind, change, inserted, correction, _generation);
            _pending.Add(edit);
            if (_pending.Count > _capacity)
            {
                // 有界：丢弃最旧的，如实计数，绝不无界增长。
                _pending.RemoveAt(0);
                _dropped++;
            }

            return edit;
        }
    }

    /// <summary>
    /// 取出当前代数下的全部暂存编辑（不可变副本），并清空。
    /// </summary>
    /// <remarks>陈旧代数的编辑在此被丢弃，不会交给新会话。</remarks>
    internal IReadOnlyList<AcpStagedEdit> DrainForGeneration(out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            if (_pending.Count == 0)
            {
                return [];
            }

            var copy = _pending.ToArray();
            _pending.Clear();
            return copy;
        }
    }

    /// <summary>清空暂存（不推进代数）。</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
