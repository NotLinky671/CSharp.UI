namespace CSharp.UI;

/// <summary>
/// 测量提案性质：区分探测测量与最终有效布局提案。
/// </summary>
/// <remarks>
/// 探测测量只用于读取自然尺寸与尺寸特征（例如弹性分配前的自然尺寸取样），
/// 不构成布局提案，也不会触发动态内容挂载/卸载；作用域退出时恢复进入前的测量状态。
/// </remarks>
internal enum MeasureProposalKind
{
    /// <summary>有效测量：结果构成最终布局提案，可被后续相同约束的有效测量复用。</summary>
    Effective = 0,

    /// <summary>探测测量：结果只在探测作用域内有效，退出时恢复此前的测量状态。</summary>
    Probe = 1,

    /// <summary>
    /// 自然尺寸（intrinsic）测量：只用于读取自然尺寸，不构成最终布局提案，
    /// 因此不能被相同约束的有效测量复用；有效测量必须重新执行并完成弹性分配。
    /// </summary>
    Intrinsic = 2,
}

/// <summary>
/// 布局轴（与堆叠方向解耦），用于按轴读取尺寸特征。
/// </summary>
internal enum LayoutAxis
{
    /// <summary>水平轴（X / 宽度）。</summary>
    Horizontal = 0,

    /// <summary>垂直轴（Y / 高度）。</summary>
    Vertical = 1,
}

/// <summary>布局特征相关的小工具。</summary>
internal static class LayoutFeatureMath
{
    /// <summary>把弹性权重规范化为有限的非负值；NaN、负数与无穷按 0 处理。</summary>
    public static double NormalizeQuota(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    /// <summary>
    /// 安全夹取：上下限逆序（最小值大于最大值）或数值非法时也不会调用 <see cref="Math.Clamp(double, double, double)"/> 抛异常。
    /// 逆序时以显式下界为准（固定/最小声明优先），调用方负责诊断该矛盾。
    /// </summary>
    public static double ClampSafe(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return double.IsNaN(min) ? 0 : min;
        }

        if (double.IsNaN(min) || double.IsNaN(max))
        {
            return value;
        }

        return min > max ? min : Math.Clamp(value, min, max);
    }
}

/// <summary>
/// 单个轴上的尺寸特征：自然尺寸、显式固定/最小/最大声明与弹性配额。
/// </summary>
/// <remarks>
/// <para>
/// 特征描述的是“声明”，不是被父约束截断后的测量结果：例如 <c>.Frame(width: 240)</c> 在 100 宽父级下
/// <see cref="ViewNode.DesiredSize"/> 只有 100，但 <see cref="ExplicitSize"/> 仍是 240。
/// 消费者不能从被截短的 <see cref="ViewNode.DesiredSize"/> 反推“可压缩性”，必须读取显式声明。
/// </para>
/// <para>
/// 盒子语义：<see cref="ExplicitSize"/>/<see cref="MinSize"/>/<see cref="MaxSize"/> 只描述该节点盒子自身的分配声明，
/// 内层内容的固定尺寸不再充当外层的 <see cref="BaseSize"/>；内容的硬最小要求单独保存在
/// <see cref="ContentMinSize"/>，用于可压缩性判断与溢出诊断。
/// 例如外层 <c>Frame(maxWidth:100)</c> 包裹固定 240 内容时基准是 100、上限是 100，同时保留“内容仍需 240”；
/// 外层 <c>Frame(minWidth:300)</c> 包裹固定 240 内容时基准是 300，内容可居中放置，不构成矛盾。
/// </para>
/// </remarks>
internal readonly record struct AxisLayoutFeatures
{
    /// <summary>只带自然尺寸的特征。</summary>
    public AxisLayoutFeatures(double naturalSize) => NaturalSize = naturalSize;

    /// <summary>该轴最近一次测量得到的自然尺寸（可能已被当前约束截断，仅作基准）。</summary>
    public double NaturalSize { get; init; }

    /// <summary>该节点盒子自身的显式固定尺寸（<c>Frame(width/height)</c>、<c>Rectangle.Size(...)</c>）；null 表示未声明。</summary>
    public double? ExplicitSize { get; init; }

    /// <summary>该节点盒子自身的显式最小值声明（<c>Frame(minWidth/minHeight)</c>）；null 表示未声明。</summary>
    public double? MinSize { get; init; }

    /// <summary>该节点盒子自身的显式最大值声明（<c>Frame(maxWidth/maxHeight)</c>）；null 表示未声明。</summary>
    public double? MaxSize { get; init; }

    /// <summary>
    /// 该节点盒子自身的硬性最小声明（来自固定盒子，例如 <c>Frame(minWidth/minHeight)</c>）；null 表示未声明。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="MinSize"/> 的区别：<see cref="MinSize"/> 是解析后的下限，可能被内层 <c>Flex</c> 的弹性下限抬高；
    /// 外层同轴 <c>Flex</c>/<c>FillWidth</c> 覆盖内层弹性配额时，只有这里的硬性声明必须保留。
    /// </remarks>
    public double? HardMinSize { get; init; }

    /// <summary>
    /// 该节点盒子自身的硬性最大声明（来自固定盒子，例如 <c>Frame(maxWidth/maxHeight)</c>）；null 表示未声明。
    /// 与 <see cref="HardMinSize"/> 对应：弹性上限可以被外层同轴弹性声明覆盖，硬性上限不可以。
    /// </summary>
    public double? HardMaxSize { get; init; }

    /// <summary>
    /// 该节点子树的硬最小要求：外框自身的固定/最小硬下界与内容要求取较大者；null 表示子树可压缩到 0。
    /// 与外框分配尺寸分开保存，避免把内层固定尺寸当成外层分配基准，同时保留“内容仍溢出”的诊断信息。
    /// </summary>
    public double? ContentMinSize { get; init; }

    /// <summary>弹性增长权重；0 表示不增长（沿用旧 <c>FlexGrow</c> 语义，由 RL-02 与 Flex 配额统一）。</summary>
    public double Grow { get; init; }

    /// <summary>弹性收缩权重；0 表示不压缩。</summary>
    public double Shrink { get; init; }

    /// <summary>弹性基准尺寸；null 表示使用自然尺寸。</summary>
    public double? Basis { get; init; }

    /// <summary>是否声明占满可用宽度（<c>FillWidth</c>，由 RL-02 的修饰符写入）。</summary>
    public bool Fill { get; init; }

    /// <summary>
    /// 该轴是否由 <c>.Flex()</c>/<c>.FillWidth()</c> 显式声明了弹性配额。
    /// 仅该标记为真的堆叠才进入弹性分配路径；旧 <c>Spacer</c>/<c>ScrollView</c> 的 <c>FlexGrow</c> 不计入，
    /// 因此未启用新能力的布局保持原有语义。
    /// </summary>
    public bool HasExplicitFlex { get; init; }

    /// <summary>该盒子是否有显式固定尺寸声明。</summary>
    public bool IsFixed => ExplicitSize.HasValue;

    /// <summary>分配可用下限：盒子自身显式固定尺寸与显式最小值的交集（都未声明时为 0）。</summary>
    public double EffectiveMin => Math.Max(ExplicitSize ?? 0, MinSize ?? 0);

    /// <summary>分配可用上限：盒子自身显式固定尺寸与显式最大值的交集（都未声明时为正无穷）。</summary>
    public double EffectiveMax => Math.Min(ExplicitSize ?? double.PositiveInfinity, MaxSize ?? double.PositiveInfinity);

    /// <summary>
    /// 是否存在矛盾：盒子自身的下限大于上限，或内容硬要求超过盒子可能的最大尺寸。
    /// <c>minWidth:300</c> 的外框容纳固定 240 内容不矛盾；<c>maxWidth:100</c> 的外框容纳固定 240 内容矛盾。
    /// </summary>
    public bool HasConflictingBounds => EffectiveMin > EffectiveMax || (ContentMinSize ?? 0) > EffectiveMax;

    /// <summary>分配前的基准尺寸：盒子自身的显式固定尺寸 &gt; 显式 basis &gt; 自然尺寸。</summary>
    public double BaseSize => ExplicitSize ?? Basis ?? NaturalSize;

    /// <summary>把内边距/外框增量叠加到全部尺寸声明上（修饰符在外层时使用）。</summary>
    public AxisLayoutFeatures Inflate(double delta) => this with
    {
        NaturalSize = NaturalSize + delta,
        ExplicitSize = ExplicitSize + delta,
        MinSize = MinSize + delta,
        MaxSize = MaxSize + delta,
        HardMinSize = HardMinSize + delta,
        HardMaxSize = HardMaxSize + delta,
        ContentMinSize = ContentMinSize + delta,
        Basis = Basis + delta,
    };
}

/// <summary>两个轴上的尺寸特征。</summary>
internal readonly record struct LayoutFeatures(AxisLayoutFeatures Horizontal, AxisLayoutFeatures Vertical)
{
    /// <summary>按轴读取特征。</summary>
    public AxisLayoutFeatures this[LayoutAxis axis]
        => axis == LayoutAxis.Horizontal ? Horizontal : Vertical;

    /// <summary>只有自然尺寸的特征（无任何显式声明）。</summary>
    public static LayoutFeatures FromNatural(Size size) => new(new AxisLayoutFeatures(size.Width), new AxisLayoutFeatures(size.Height));

    /// <summary>替换其中一个轴的特征。</summary>
    public LayoutFeatures WithAxis(LayoutAxis axis, AxisLayoutFeatures features)
        => axis == LayoutAxis.Horizontal ? this with { Horizontal = features } : this with { Vertical = features };
}
