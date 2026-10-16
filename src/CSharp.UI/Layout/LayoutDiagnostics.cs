namespace CSharp.UI;

/// <summary>布局诊断类别。</summary>
internal enum LayoutDiagnosticKind
{
    /// <summary>溢出：显式固定/最小尺寸大于父级可用空间。</summary>
    Overflow = 0,

    /// <summary>无界：需要有限可用空间的操作遇到了无界约束。</summary>
    Unbounded = 1,

    /// <summary>不支持：当前容器不接受某种直接子项声明（例如 Flow 直接子项的 Flex 权重）。</summary>
    Unsupported = 2,
}

/// <summary>布局诊断的去重键：同一节点、同一类别、同一轴视为同一类稳定问题。</summary>
internal readonly record struct LayoutDiagnosticKey(ViewNode Node, LayoutDiagnosticKind Kind, LayoutAxis Axis);

/// <summary>
/// 有界的布局诊断收集器：同一节点/类别/轴只输出一次，且每一轮布局限制新增输出条数，
/// 避免稳定问题在窗口连续重绘时逐帧刷屏。
/// </summary>
/// <remarks>
/// 去重表以节点引用为键、容量固定；超出容量时按最早记录淘汰。被去重或超出本轮配额的报告计入
/// <see cref="SuppressedCount"/>，便于测试确认“稳定问题不会逐帧重复记录”。
/// </remarks>
internal sealed class LayoutDiagnostics
{
    /// <summary>一轮布局最多输出的新诊断条数（稳定问题之外的新问题按轮次摊开）。</summary>
    public const int MaxReportsPerPass = 16;

    /// <summary>去重表默认容量上限。</summary>
    public const int DefaultCapacity = 128;

    private readonly HashSet<LayoutDiagnosticKey> _reported = [];
    private readonly Queue<LayoutDiagnosticKey> _order = new();
    private readonly Action<string>? _sink;
    private int _reportedThisPass;
    private int _suppressed;

    /// <summary>由输出回调与去重容量创建。</summary>
    public LayoutDiagnostics(Action<string>? sink = null, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _sink = sink;
        Capacity = capacity;
    }

    /// <summary>去重表容量上限（有界）。</summary>
    public int Capacity { get; }

    /// <summary>去重表中当前保留的记录数，永不超过 <see cref="Capacity"/>。</summary>
    public int ReportedCount => _reported.Count;

    /// <summary>被去重或本轮配额抑制的报告次数。</summary>
    public int SuppressedCount => _suppressed;

    /// <summary>开始一轮布局，重置本轮输出配额。</summary>
    public void BeginPass() => _reportedThisPass = 0;

    /// <summary>报告一条诊断；返回 true 表示本次实际输出。</summary>
    public bool Report(LayoutDiagnosticKey key, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_reported.Contains(key))
        {
            _suppressed++;
            return false;
        }

        if (_reportedThisPass >= MaxReportsPerPass)
        {
            _suppressed++;
            return false;
        }

        _reported.Add(key);
        _order.Enqueue(key);
        while (_reported.Count > Capacity && _order.Count > 0)
        {
            _reported.Remove(_order.Dequeue());
        }

        _reportedThisPass++;
        _sink?.Invoke(message);
        return true;
    }

    /// <summary>清空去重记录与统计（用于显式重置诊断）。</summary>
    public void Clear()
    {
        _reported.Clear();
        _order.Clear();
        _reportedThisPass = 0;
        _suppressed = 0;
    }
}
