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
    public static void Measure(
        ViewNode node,
        LayoutConstraints constraints,
        LayoutContext context,
        StackOrientation orientation,
        double spacing,
        Alignment crossAlignment)
    {
        var horizontal = orientation == StackOrientation.Horizontal;

        // 子项约束：主轴不限制，交叉轴只继承父级上限。
        // 不继承父级最小值，否则父级紧约束会掩盖子项的自然尺寸（拉伸由排列阶段决定）。
        var childConstraints = horizontal
            ? new LayoutConstraints(0, double.PositiveInfinity, 0, constraints.MaxHeight)
            : new LayoutConstraints(0, constraints.MaxWidth, 0, double.PositiveInfinity);

        double main = 0;
        double cross = 0;
        var count = 0;
        foreach (var child in node.ChildList)
        {
            context.Measure(child, childConstraints);
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

            count++;
        }

        if (count > 1)
        {
            main += spacing * (count - 1);
        }

        var mainSize = Math.Max(main, horizontal ? constraints.MinWidth : constraints.MinHeight);
        var crossSize = Math.Max(cross, horizontal ? constraints.MinHeight : constraints.MinWidth);

        if (horizontal)
        {
            node.DesiredSize = new Size(
                constraints.IsWidthTight ? constraints.MaxWidth : mainSize,
                constraints.IsHeightTight ? constraints.MaxHeight : crossSize);
        }
        else
        {
            node.DesiredSize = new Size(
                constraints.IsWidthTight ? constraints.MaxWidth : crossSize,
                constraints.IsHeightTight ? constraints.MaxHeight : mainSize);
        }
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
}
