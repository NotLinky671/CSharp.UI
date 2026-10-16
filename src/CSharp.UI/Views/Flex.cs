namespace CSharp.UI;

/// <summary>弹性配额作用的轴。</summary>
public enum FlexAxis
{
    /// <summary>由最近的布局容器主轴决定（VStack 为垂直、HStack 为水平）。</summary>
    Main = 0,

    /// <summary>固定作用于水平轴（宽度），容器切换主轴时不变。</summary>
    Horizontal = 1,

    /// <summary>固定作用于垂直轴（高度），容器切换主轴时不变。</summary>
    Vertical = 2,
}

/// <summary>
/// 弹性修饰符：在最近的堆叠容器中按剩余空间增长或压缩。
/// </summary>
/// <remarks>
/// <para>
/// <c>content.Flex(grow: 1, shrink: 1, basis: 240, min: 120)</c>：<c>basis</c> 省略表示自然尺寸，
/// <c>min</c> 默认 0、<c>max</c> 默认正无穷，<c>axis</c> 默认 <see cref="FlexAxis.Main"/>。
/// 需要“通常 240，但可以变窄”时使用 <c>basis</c>；<c>.Frame(width: 240)</c> 仍是固定尺寸，不会被弹性分配压缩或拉伸。
/// </para>
/// <para>
/// 同轴嵌套多个 <c>Flex</c>/<c>FillWidth</c> 时外层声明生效，权重不叠加；不同轴独立处理。
/// 参数在声明时校验：权重与显式 basis/min 必须有限且非负，<c>max</c> 允许正无穷，禁止 NaN，且 <c>min ≤ max</c>。
/// </para>
/// </remarks>
public sealed class FlexView : ModifierView
{
    /// <summary>由内容与弹性配额创建；非法参数立即抛出。</summary>
    public FlexView(
        View content,
        double grow = 1,
        double shrink = 1,
        double? basis = null,
        double min = 0,
        double max = double.PositiveInfinity,
        FlexAxis axis = FlexAxis.Main)
        : base(content)
    {
        FlexValidation.Validate(grow, shrink, basis, min, max, axis);
        Grow = grow;
        Shrink = shrink;
        Basis = basis;
        Min = min;
        Max = max;
        Axis = axis;
    }

    /// <summary>有正剩余空间时的增长权重；0 表示不增长。</summary>
    public double Grow { get; }

    /// <summary>空间不足时的压缩权重；0 表示不压缩。</summary>
    public double Shrink { get; }

    /// <summary>弹性基准尺寸；null 表示自然尺寸。</summary>
    public double? Basis { get; }

    /// <summary>压缩下限。</summary>
    public double Min { get; }

    /// <summary>增长上限；默认正无穷。</summary>
    public double Max { get; }

    /// <summary>配额作用的轴。</summary>
    public FlexAxis Axis { get; }

    /// <inheritdoc />
    public override string DebugName => $"Flex({Axis}, grow:{Grow:0.##}, shrink:{Shrink:0.##})";

    /// <inheritdoc />
    internal override LayoutFeatures GetLayoutFeatures(ViewNode node, LayoutAxis mainAxis)
    {
        var inner = base.GetLayoutFeatures(node, mainAxis);
        var axis = FlexValidation.ResolveAxis(Axis, mainAxis);
        var current = inner[axis];

        // 外层同轴声明完整覆盖内层的弹性配额（不叠加、不叠加限制）：权重、基准与弹性上下限都由外层决定，
        // 只保留硬性 Frame 声明的边界（HardMinSize/HardMaxSize），避免内层 min 污染外层分配。
        // 内层 FillWidth 的填充标记也属于被覆盖的弹性声明：外层普通 Flex 不应继续被当作交叉轴填充请求。
        var updated = current with
        {
            Grow = Grow,
            Shrink = Shrink,
            Basis = Basis,
            MinSize = FlexValidation.MaxOf(current.HardMinSize, Min),
            MaxSize = FlexValidation.MinOf(current.HardMaxSize, Max),
            Fill = false,
            HasExplicitFlex = true,
        };

        return inner.WithAxis(axis, updated);
    }

    /// <inheritdoc />
    internal override bool LayoutPropertiesEquivalent(View other)
        => other is FlexView flex
            && flex.Grow == Grow
            && flex.Shrink == Shrink
            && flex.Basis == Basis
            && flex.Min == Min
            && flex.Max == Max
            && flex.Axis == Axis;
}

/// <summary>
/// 占满可用宽度的便利修饰符：有界宽度时填满，横向 Stack 主轴上作为可增长、可压缩项参与分配；
/// 宽度无界时回退自然尺寸。它不取消子树内已经明确声明的固定 <c>Frame</c>。
/// </summary>
public sealed class FillWidthView : ModifierView
{
    /// <summary>由内容创建。</summary>
    public FillWidthView(View content)
        : base(content)
    {
    }

    /// <inheritdoc />
    public override string DebugName => "FillWidth";

    /// <inheritdoc />
    internal override LayoutFeatures GetLayoutFeatures(ViewNode node, LayoutAxis mainAxis)
    {
        var inner = base.GetLayoutFeatures(node, mainAxis);

        // FillWidth 只作用于水平轴：横向 Stack 主轴上参与增长/压缩，纵向 Stack 交叉轴上占满可用宽度。
        // 它同样完整覆盖内层同轴弹性声明：内层 Flex 的显式 basis 与 min/max 都不再生效（基准回到自然尺寸），
        // 只保留硬性 Frame 声明；横向无界时因此按契约回退自然尺寸。
        var horizontal = inner.Horizontal with
        {
            Grow = 1,
            Shrink = 1,
            Basis = null,
            MinSize = inner.Horizontal.HardMinSize,
            MaxSize = inner.Horizontal.HardMaxSize,
            Fill = true,
            HasExplicitFlex = true,
        };

        return inner.WithAxis(LayoutAxis.Horizontal, horizontal);
    }

    /// <inheritdoc />
    internal override bool LayoutPropertiesEquivalent(View other) => other is FillWidthView;
}

/// <summary>弹性参数校验与轴解析。</summary>
internal static class FlexValidation
{
    /// <summary>校验弹性配额；非法输入立即抛出明确异常。</summary>
    public static void Validate(double grow, double shrink, double? basis, double min, double max, FlexAxis axis)
    {
        if (!double.IsFinite(grow) || grow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grow), grow, "grow 必须是有限的非负值。");
        }

        if (!double.IsFinite(shrink) || shrink < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shrink), shrink, "shrink 必须是有限的非负值。");
        }

        if (basis is { } basisValue && (!double.IsFinite(basisValue) || basisValue < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(basis), basisValue, "basis 必须为 null 或有限的非负值。");
        }

        if (!double.IsFinite(min) || min < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "min 必须是有限的非负值。");
        }

        if (double.IsNaN(max) || max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "max 必须是非负值（允许正无穷）。");
        }

        if (min > max)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, $"min({min}) 不得大于 max({max})。");
        }

        if (!Enum.IsDefined(axis))
        {
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "未知的 FlexAxis 取值。");
        }
    }

    /// <summary>把声明的轴解析为具体布局轴；<see cref="FlexAxis.Main"/> 取自最近布局容器的主轴。</summary>
    public static LayoutAxis ResolveAxis(FlexAxis axis, LayoutAxis mainAxis) => axis switch
    {
        FlexAxis.Horizontal => LayoutAxis.Horizontal,
        FlexAxis.Vertical => LayoutAxis.Vertical,
        _ => mainAxis,
    };

    /// <summary>两个下限取较大者（null 表示未声明）。</summary>
    public static double? MaxOf(double? left, double? right)
        => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    /// <summary>两个上限取较小者（null 表示未声明）。</summary>
    public static double? MinOf(double? left, double? right)
        => left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
}
