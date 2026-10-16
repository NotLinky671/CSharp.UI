namespace CSharp.UI;

/// <summary>堆叠方向。</summary>
public enum StackOrientation
{
    /// <summary>垂直堆叠（主轴为 Y）。</summary>
    Vertical,

    /// <summary>水平堆叠（主轴为 X）。</summary>
    Horizontal,
}

/// <summary>
/// 垂直堆叠布局：子项沿 Y 轴依次排列，支持间距、交叉轴对齐与 <see cref="Spacer"/> 分配剩余空间。
/// </summary>
public sealed class VStack : View
{
    /// <summary>由子项创建。</summary>
    public VStack(params View[] children)
        : this((IReadOnlyList<View>)children)
    {
    }

    /// <summary>由子项集合创建。</summary>
    public VStack(IReadOnlyList<View> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Items = children;
    }

    /// <summary>子项（按排列与绘制顺序）。</summary>
    public IReadOnlyList<View> Items { get; init; }

    /// <summary>子项间距（逻辑像素）。</summary>
    public double Spacing { get; init; }

    /// <summary>交叉轴（水平）对齐；默认拉伸填满。</summary>
    public Alignment HorizontalAlignment { get; init; } = Alignment.Stretch;

    /// <inheritdoc />
    public override string DebugName => $"VStack({Items.Count})";

    internal override IReadOnlyList<View> Children => Items;

    /// <summary>返回设置间距的副本。</summary>
    public VStack WithSpacing(double spacing)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spacing);
        return new VStack(Items) { Spacing = spacing, HorizontalAlignment = HorizontalAlignment };
    }

    /// <summary>返回设置水平对齐的副本。</summary>
    public VStack Align(Alignment alignment) => new(Items) { Spacing = Spacing, HorizontalAlignment = alignment };

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is VStack stack && stack.Spacing == Spacing && stack.HorizontalAlignment == HorizontalAlignment;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        => StackLayout.Measure(node, constraints, context, StackOrientation.Vertical, Spacing, HorizontalAlignment);

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        StackLayout.Arrange(node, bounds, context, StackOrientation.Vertical, Spacing, HorizontalAlignment);
    }
}

/// <summary>
/// 水平堆叠布局：子项沿 X 轴依次排列，支持间距、交叉轴对齐与 <see cref="Spacer"/> 分配剩余空间。
/// </summary>
public sealed class HStack : View
{
    /// <summary>由子项创建。</summary>
    public HStack(params View[] children)
        : this((IReadOnlyList<View>)children)
    {
    }

    /// <summary>由子项集合创建。</summary>
    public HStack(IReadOnlyList<View> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Items = children;
    }

    /// <summary>子项（按排列与绘制顺序）。</summary>
    public IReadOnlyList<View> Items { get; init; }

    /// <summary>子项间距（逻辑像素）。</summary>
    public double Spacing { get; init; }

    /// <summary>交叉轴（垂直）对齐；默认居中。</summary>
    public Alignment VerticalAlignment { get; init; } = Alignment.Center;

    /// <inheritdoc />
    public override string DebugName => $"HStack({Items.Count})";

    internal override IReadOnlyList<View> Children => Items;

    /// <summary>返回设置间距的副本。</summary>
    public HStack WithSpacing(double spacing)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spacing);
        return new HStack(Items) { Spacing = spacing, VerticalAlignment = VerticalAlignment };
    }

    /// <summary>返回设置垂直对齐的副本。</summary>
    public HStack Align(Alignment alignment) => new(Items) { Spacing = Spacing, VerticalAlignment = alignment };

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is HStack stack && stack.Spacing == Spacing && stack.VerticalAlignment == VerticalAlignment;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        => StackLayout.Measure(node, constraints, context, StackOrientation.Horizontal, Spacing, VerticalAlignment);

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        StackLayout.Arrange(node, bounds, context, StackOrientation.Horizontal, Spacing, VerticalAlignment);
    }
}

/// <summary>
/// 层叠布局：所有子项排列在同一矩形内，按描述顺序绘制（后者在上层）。
/// </summary>
/// <remarks>默认 <see cref="Alignment.Stretch"/>，因此背景层会填满容器。</remarks>
public sealed class ZStack : View
{
    /// <summary>由子项创建。</summary>
    public ZStack(params View[] children)
        : this((IReadOnlyList<View>)children)
    {
    }

    /// <summary>由子项集合创建。</summary>
    public ZStack(IReadOnlyList<View> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Items = children;
    }

    /// <summary>子项（按绘制顺序，后者在上层）。</summary>
    public IReadOnlyList<View> Items { get; init; }

    /// <summary>子项对齐方式；默认拉伸。</summary>
    public Alignment Alignment { get; init; } = Alignment.Stretch;

    /// <inheritdoc />
    public override string DebugName => $"ZStack({Items.Count})";

    internal override IReadOnlyList<View> Children => Items;

    /// <summary>返回设置对齐的副本。</summary>
    public ZStack Align(Alignment alignment) => new(Items) { Alignment = alignment };

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is ZStack stack && stack.Alignment == Alignment;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        double width = 0;
        double height = 0;
        foreach (var child in node.ChildList)
        {
            context.Measure(child, constraints);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        node.DesiredSize = constraints.Constrain(new Size(
            Math.Max(width, constraints.MinWidth),
            Math.Max(height, constraints.MinHeight)));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        foreach (var child in node.ChildList)
        {
            var rect = Alignment == Alignment.Stretch
                ? bounds
                : bounds.AlignInside(child.DesiredSize, Alignment);
            context.Arrange(child, rect);
        }
    }
}

/// <summary>堆叠布局的链式扩展：<c>new VStack(...).Spacing(12)</c>。</summary>
/// <remarks>
/// <see cref="VStack.Spacing"/> 是属性，方法形式通过扩展提供，使目标写法与设计文档一致。
/// </remarks>
public static class StackExtensions
{
    /// <summary>设置垂直堆叠间距。</summary>
    public static VStack Spacing(this VStack stack, double spacing)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.WithSpacing(spacing);
    }

    /// <summary>设置水平堆叠间距。</summary>
    public static HStack Spacing(this HStack stack, double spacing)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.WithSpacing(spacing);
    }
}

/// <summary>堆叠布局的共享测量/排列实现。</summary>
internal static class StackLayout
{
    private const double Epsilon = 0.0001;

    public static void Measure(
        ViewNode node,
        LayoutConstraints constraints,
        LayoutContext context,
        StackOrientation orientation,
        double spacing,
        Alignment crossAlignment)
    {
        var horizontal = orientation == StackOrientation.Horizontal;
        var mainAxis = horizontal ? LayoutAxis.Horizontal : LayoutAxis.Vertical;
        var children = node.ChildList;

        // 子项约束：主轴不限制，交叉轴只继承父级上限。
        // 不继承父级最小值，否则父级紧约束会掩盖子项的自然尺寸（拉伸由排列阶段决定）。
        var childConstraints = horizontal
            ? new LayoutConstraints(0, double.PositiveInfinity, 0, constraints.MaxHeight)
            : new LayoutConstraints(0, constraints.MaxWidth, 0, double.PositiveInfinity);

        // 1. 自然尺寸/基准测量：此模式下嵌套容器不做最终分配，避免逐层完整分配导致指数级重测。
        //    自然测量只登记 Intrinsic 提案，不能被后续相同约束的有效测量复用。
        var alreadyIntrinsic = context.IsIntrinsicMeasure;
        context.PushIntrinsicMeasure();
        try
        {
            foreach (var child in children)
            {
                context.Measure(child, childConstraints);
            }
        }
        finally
        {
            context.PopIntrinsicMeasure();
        }

        if (alreadyIntrinsic)
        {
            // 自然尺寸测量：只求和自然尺寸，不做弹性分配。
            node.ChildMainAllocation = null;
            node.AllocatedMainExtent = double.NaN;
            var (naturalMain, naturalCross) = SumNatural(children, horizontal, spacing);
            node.DesiredSize = Compose(constraints, horizontal, naturalMain, naturalCross);
            return;
        }

        if (!HasExplicitFlex(children, mainAxis))
        {
            // 旧语义：只求和自然尺寸，剩余空间由 Arrange 按旧 FlexGrow 分配。
            // 自身没有直接弹性声明时仍必须把有效提案传递到内部含 Flex 的子树，
            // 否则嵌套 Flex 会永远停留在自然测量结果上。
            foreach (var child in children)
            {
                if (context.SubtreeHasFlexDeclaration(child))
                {
                    context.Measure(child, childConstraints);
                }
            }

            node.ChildMainAllocation = null;
            node.AllocatedMainExtent = double.NaN;

            // 复测可能改变子项尺寸（例如内部换行文本在最终宽度下变高），
            // 必须用复测后的最终尺寸重新汇总自己的 DesiredSize，否则向上传递的是旧的自然高度。
            var (finalMain, finalCross) = SumNatural(children, horizontal, spacing);
            node.DesiredSize = Compose(constraints, horizontal, finalMain, finalCross);
            return;
        }

        // 2. 弹性分配，并按最终主轴长度复测全部子项（含非弹性子项，保证它们也拿到最终提案）。
        var items = BuildItems(children, mainAxis);
        var available = horizontal ? constraints.MaxWidth : constraints.MaxHeight;
        var bounded = horizontal ? constraints.HasBoundedWidth : constraints.HasBoundedHeight;
        var sizes = Allocate(items, bounded ? available : double.PositiveInfinity, spacing, context, node, mainAxis);

        for (var i = 0; i < items.Count; i++)
        {
            context.Measure(items[i].Node, TightMain(childConstraints, horizontal, sizes[i]));
        }

        node.ChildMainAllocation = sizes;
        node.AllocatedMainExtent = available;

        var crossSize = 0.0;
        foreach (var child in children)
        {
            crossSize = Math.Max(crossSize, horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
        }

        var allocatedMain = Sum(sizes);
        if (sizes.Length > 1)
        {
            allocatedMain += spacing * (sizes.Length - 1);
        }

        node.DesiredSize = Compose(constraints, horizontal, allocatedMain, crossSize);
    }

    public static void Arrange(
        ViewNode node,
        Rect bounds,
        LayoutContext context,
        StackOrientation orientation,
        double spacing,
        Alignment crossAlignment)
    {
        var horizontal = orientation == StackOrientation.Horizontal;
        var available = horizontal ? bounds.Width : bounds.Height;
        var children = node.ChildList;

        // 弹性路径：使用测量阶段冻结的分配结果；主轴长度与测量一致（或测量时主轴无界）时不再重算。
        if (node.ChildMainAllocation is { } allocation
            && allocation.Length == children.Count
            && (double.IsInfinity(node.AllocatedMainExtent) || Math.Abs(node.AllocatedMainExtent - available) <= 0.001))
        {
            ArrangeFlex(node, bounds, context, horizontal, allocation, spacing, crossAlignment);
            return;
        }

        // 1. 统计固定尺寸与弹性权重。
        double fixedSize = 0;
        double totalFlex = 0;
        foreach (var child in children)
        {
            var flex = child.View.FlexGrow(child, horizontal);
            if (flex > 0)
            {
                totalFlex += flex;
            }
            else
            {
                fixedSize += horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            }
        }

        if (children.Count > 1)
        {
            fixedSize += spacing * (children.Count - 1);
        }

        var leftover = Math.Max(0, available - fixedSize);

        // 2. 依次排列。
        var offset = horizontal ? bounds.X : bounds.Y;
        foreach (var child in children)
        {
            var flex = child.View.FlexGrow(child, horizontal);
            var baseSize = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            var mainSize = flex > 0 && totalFlex > 0
                ? leftover * (flex / totalFlex)
                : baseSize;

            if (horizontal)
            {
                var height = crossAlignment == Alignment.Stretch
                    ? bounds.Height
                    : Math.Min(child.DesiredSize.Height, bounds.Height);
                var y = bounds.Y + (crossAlignment switch
                {
                    Alignment.Center => (bounds.Height - height) / 2,
                    Alignment.End => bounds.Height - height,
                    _ => 0,
                });
                context.Arrange(child, new Rect(Math.Round(offset, 3), Math.Round(y, 3), mainSize, height));
            }
            else
            {
                var width = crossAlignment == Alignment.Stretch
                    ? bounds.Width
                    : Math.Min(child.DesiredSize.Width, bounds.Width);
                var x = bounds.X + (crossAlignment switch
                {
                    Alignment.Center => (bounds.Width - width) / 2,
                    Alignment.End => bounds.Width - width,
                    _ => 0,
                });
                context.Arrange(child, new Rect(Math.Round(x, 3), Math.Round(offset, 3), width, mainSize));
            }

            offset += mainSize + spacing;
        }
    }

    /// <summary>是否存在显式 <c>.Flex()</c>/<c>.FillWidth()</c> 声明（旧 FlexGrow 不计入）。</summary>
    private static bool HasExplicitFlex(List<ViewNode> children, LayoutAxis mainAxis)
    {
        foreach (var child in children)
        {
            var features = child.GetLayoutFeatures(mainAxis);
            if (features.Horizontal.HasExplicitFlex || features.Vertical.HasExplicitFlex)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>旧语义：自然尺寸求和（主轴含间距）。</summary>
    private static (double Main, double Cross) SumNatural(List<ViewNode> children, bool horizontal, double spacing)
    {
        var main = 0.0;
        var cross = 0.0;
        foreach (var child in children)
        {
            if (horizontal)
            {
                main += child.DesiredSize.Width;
                cross = Math.Max(cross, child.DesiredSize.Height);
            }
            else
            {
                main += child.DesiredSize.Height;
                cross = Math.Max(cross, child.DesiredSize.Width);
            }
        }

        if (children.Count > 1)
        {
            main += spacing * (children.Count - 1);
        }

        return (main, cross);
    }

    private static Size Compose(LayoutConstraints constraints, bool horizontal, double main, double cross)
    {
        var mainSize = Math.Max(main, horizontal ? constraints.MinWidth : constraints.MinHeight);
        var crossSize = Math.Max(cross, horizontal ? constraints.MinHeight : constraints.MinWidth);

        return horizontal
            ? new Size(
                constraints.IsWidthTight ? constraints.MaxWidth : mainSize,
                constraints.IsHeightTight ? constraints.MaxHeight : crossSize)
            : new Size(
                constraints.IsWidthTight ? constraints.MaxWidth : crossSize,
                constraints.IsHeightTight ? constraints.MaxHeight : mainSize);
    }

    private static List<StackItem> BuildItems(List<ViewNode> children, LayoutAxis mainAxis)
    {
        var items = new List<StackItem>(children.Count);
        foreach (var child in children)
        {
            var features = child.GetLayoutFeatures(mainAxis)[mainAxis];
            var natural = mainAxis == LayoutAxis.Horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            items.Add(new StackItem
            {
                Node = child,
                Natural = Sanitize(natural),
                Min = Sanitize(features.EffectiveMin),
                Max = double.IsNaN(features.EffectiveMax) ? double.PositiveInfinity : features.EffectiveMax,
                Base = features.BaseSize,
                Grow = LayoutFeatureMath.NormalizeQuota(features.Grow),
                Shrink = LayoutFeatureMath.NormalizeQuota(features.Shrink),
                HasExplicitFlex = features.HasExplicitFlex,
                HasDeclaredBase = features.ExplicitSize.HasValue || features.Basis.HasValue,
            });
        }

        return items;
    }

    private static double Sanitize(double value)
        => double.IsFinite(value) && value > 0 ? value : 0;

    /// <summary>把子项约束的主轴改为“恰好为分配长度”的紧约束，用于分配后的最终复测。</summary>
    private static LayoutConstraints TightMain(LayoutConstraints childConstraints, bool horizontal, double size)
    {
        var value = Sanitize(size);
        return horizontal
            ? new LayoutConstraints(value, value, childConstraints.MinHeight, childConstraints.MaxHeight)
            : new LayoutConstraints(childConstraints.MinWidth, childConstraints.MaxWidth, value, value);
    }

    /// <summary>
    /// 按约束后的 basis 计算各子项的主轴尺寸：先按增长权重分配正剩余空间、按压缩权重分配不足空间，
    /// 达到 min/max 的项目冻结后把剩余量分配给其它项目；无法容纳时报告溢出且不产生负尺寸。
    /// </summary>
    private static double[] Allocate(
        List<StackItem> items,
        double available,
        double spacing,
        LayoutContext context,
        ViewNode stackNode,
        LayoutAxis axis)
    {
        var sizes = new double[items.Count];

        // 压缩权重使用“初始经 min/max 约束后的 basis”，并在冻结再分配过程中保持不变；
        // 否则 basis:1000/max:100 会按 1000:100 的原始权重压缩，得到错误的 ~9/91。
        var shrinkBasis = new double[items.Count];
        var bounded = double.IsFinite(available);
        for (var i = 0; i < items.Count; i++)
        {
            sizes[i] = StartSize(items[i], bounded);
            shrinkBasis[i] = sizes[i];
        }

        if (!bounded)
        {
            // 主轴无界：不做无穷剩余空间分配，显式 basis 保留、其余使用自然尺寸。
            return sizes;
        }

        var spacingTotal = items.Count > 1 ? spacing * (items.Count - 1) : 0;
        var contentAvailable = Math.Max(0, available - spacingTotal);
        var total = Sum(sizes);

        if (total < contentAvailable - Epsilon)
        {
            Grow(items, sizes, contentAvailable - total);
        }
        else if (total > contentAvailable + Epsilon)
        {
            Shrink(items, sizes, shrinkBasis, total - contentAvailable, contentAvailable, context, stackNode, axis);
        }

        return sizes;
    }

    private static double StartSize(StackItem item, bool bounded)
    {
        if (bounded && item.HasExplicitFlex)
        {
            return LayoutFeatureMath.ClampSafe(item.Base, item.Min, item.Max);
        }

        if (!bounded && item.HasDeclaredBase)
        {
            return LayoutFeatureMath.ClampSafe(item.Base, item.Min, item.Max);
        }

        return item.Natural;
    }

    private static void Grow(List<StackItem> items, double[] sizes, double free)
    {
        var guard = 0;
        while (free > Epsilon && guard++ <= items.Count + 1)
        {
            var weightSum = 0.0;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Grow > 0 && sizes[i] < items[i].Max - Epsilon)
                {
                    weightSum += items[i].Grow;
                }
            }

            if (weightSum <= 0)
            {
                return;
            }

            var distributed = 0.0;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Grow <= 0 || sizes[i] >= items[i].Max - Epsilon)
                {
                    continue;
                }

                var share = free * (items[i].Grow / weightSum);
                var next = Math.Min(items[i].Max, sizes[i] + share);
                distributed += next - sizes[i];
                sizes[i] = next;
            }

            if (distributed <= Epsilon)
            {
                return;
            }

            free -= distributed;
        }
    }

    private static void Shrink(
        List<StackItem> items,
        double[] sizes,
        double[] shrinkBasis,
        double overflow,
        double contentAvailable,
        LayoutContext context,
        ViewNode stackNode,
        LayoutAxis axis)
    {
        var remaining = overflow;
        var guard = 0;
        while (remaining > Epsilon && guard++ <= items.Count + 1)
        {
            var weightSum = 0.0;
            for (var i = 0; i < items.Count; i++)
            {
                if (CanShrink(items[i], sizes[i]))
                {
                    weightSum += items[i].Shrink * Math.Max(0, shrinkBasis[i]);
                }
            }

            if (weightSum <= 0)
            {
                break;
            }

            var reduced = 0.0;
            for (var i = 0; i < items.Count; i++)
            {
                if (!CanShrink(items[i], sizes[i]))
                {
                    continue;
                }

                var weight = items[i].Shrink * Math.Max(0, shrinkBasis[i]);
                if (weight <= 0)
                {
                    continue;
                }

                var share = remaining * (weight / weightSum);
                var next = Math.Max(items[i].Min, sizes[i] - share);
                reduced += sizes[i] - next;
                sizes[i] = next;
            }

            if (reduced <= Epsilon)
            {
                break;
            }

            remaining -= reduced;
        }

        if (remaining > Epsilon && contentAvailable > Epsilon)
        {
            // 固定项与 min 之和仍超过容器：给出可定位诊断，不产生负尺寸、不丢控件。
            context.ReportOverflow(stackNode, axis, Sum(sizes), contentAvailable);
        }
    }

    private static bool CanShrink(StackItem item, double size)
        => item.Shrink > 0 && size > item.Min + Epsilon;

    private static double Sum(double[] values)
    {
        var total = 0.0;
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    private static void ArrangeFlex(
        ViewNode node,
        Rect bounds,
        LayoutContext context,
        bool horizontal,
        double[] allocation,
        double spacing,
        Alignment crossAlignment)
    {
        var mainAxis = horizontal ? LayoutAxis.Horizontal : LayoutAxis.Vertical;
        var crossAxis = horizontal ? LayoutAxis.Vertical : LayoutAxis.Horizontal;
        var availableCross = horizontal ? bounds.Height : bounds.Width;
        var children = node.ChildList;
        var offset = horizontal ? bounds.X : bounds.Y;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var crossFeature = child.GetLayoutFeatures(mainAxis)[crossAxis];
            var cross = ResolveCross(child, crossFeature, availableCross, crossAlignment, crossAxis);
            var mainSize = Math.Max(0, allocation[i]);

            // 交叉轴 Offset 是相对容器内容区的偏移：必须叠加容器自身原点，否则父级有偏移（例如 Padding）时子项会回到 0。
            if (horizontal)
            {
                context.Arrange(child, new Rect(
                    Math.Round(offset, 3),
                    Math.Round(bounds.Y + cross.Offset, 3),
                    mainSize,
                    cross.Size));
            }
            else
            {
                context.Arrange(child, new Rect(
                    Math.Round(bounds.X + cross.Offset, 3),
                    Math.Round(offset, 3),
                    cross.Size,
                    mainSize));
            }

            offset += mainSize + spacing;
        }
    }

    /// <summary>
    /// 交叉轴尺寸：<c>Stretch</c> 或声明了 <c>FillWidth</c> 时占满可用空间，但不得超过子项自身声明的最大尺寸；
    /// 其它对齐沿用“自然尺寸与可用空间取较小值”。逆序边界使用安全夹取，不会抛异常。
    /// </summary>
    private static (double Size, double Offset) ResolveCross(
        ViewNode child,
        AxisLayoutFeatures feature,
        double available,
        Alignment alignment,
        LayoutAxis crossAxis)
    {
        var stretch = alignment == Alignment.Stretch || feature.Fill;
        var natural = crossAxis == LayoutAxis.Horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
        var requested = stretch ? available : Math.Min(natural, available);
        var size = Math.Max(0, LayoutFeatureMath.ClampSafe(requested, feature.EffectiveMin, feature.EffectiveMax));

        var offset = stretch
            ? 0
            : alignment switch
            {
                Alignment.Center => (available - size) / 2,
                Alignment.End => available - size,
                _ => 0,
            };

        return (Math.Round(size, 3), Math.Round(Math.Max(0, offset), 3));
    }

    /// <summary>弹性分配使用的一个子项主轴特征快照。</summary>
    private struct StackItem
    {
        public ViewNode Node;
        public double Natural;
        public double Min;
        public double Max;
        public double Base;
        public double Grow;
        public double Shrink;
        public bool HasExplicitFlex;
        public bool HasDeclaredBase;
    }
}
