namespace CSharp.UI;

/// <summary>
/// 稳定身份的自适应堆叠：父级有效逻辑宽度小于 <see cref="Breakpoint"/> 时纵向排列，否则横向排列。
/// </summary>
/// <remarks>
/// <para>
/// 该容器始终保持同一个视图类型与同一批子节点，只切换测量/排列策略，不通过替换 HStack/VStack 子树实现，
/// 因此输入框文本、光标、选区、焦点、组件 <c>UseState</c> 与滚动状态在宽窄切换时保持。
/// </para>
/// <para>
/// 断点依据父级扣除 Padding 等修饰后提供的逻辑宽度：<c>width &lt; breakpoint</c> 为窄（纵向），
/// <c>width &gt;= breakpoint</c> 为宽（横向）；宽度无界时按横向自然尺寸处理，不读取窗口或 DPI。
/// </para>
/// <para>
/// 复用统一的 <see cref="StackLayout"/> 算法与已冻结的轴向特征：<c>FlexAxis.Main</c> 随当前模式的最近 Stack 主轴解释，
/// 显式 <c>FlexAxis.Horizontal</c> 的宽度 basis 在纵向模式下不会变成高度。
/// </para>
/// </remarks>
public sealed class AdaptiveStack : View
{
    private readonly double _breakpoint;
    private readonly double _spacing;
    private readonly double _verticalSpacing;
    private readonly Alignment _wideAlignment = Alignment.Center;
    private readonly Alignment _narrowAlignment = Alignment.Stretch;

    /// <summary>由断点与子项创建。</summary>
    public AdaptiveStack(double breakpoint, params View[] children)
        : this(breakpoint, (IReadOnlyList<View>)children)
    {
    }

    /// <summary>由断点与子项集合创建。</summary>
    public AdaptiveStack(double breakpoint, IReadOnlyList<View> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Breakpoint = breakpoint;
        Items = children;
    }

    /// <summary>子项（按排列与绘制顺序）。</summary>
    public IReadOnlyList<View> Items { get; init; }

    /// <summary>宽窄切换的父级有效逻辑宽度阈值；必须有限且为正。</summary>
    public double Breakpoint
    {
        get => _breakpoint;
        init => _breakpoint = ResponsiveValidation.PositiveFinite(value, nameof(Breakpoint));
    }

    /// <summary>宽模式（横向）的子项间距；必须有限且非负。</summary>
    public double Spacing
    {
        get => _spacing;
        init => _spacing = ResponsiveValidation.NonNegativeFinite(value, nameof(Spacing));
    }

    /// <summary>窄模式（纵向）的子项间距；必须有限且非负。</summary>
    public double VerticalSpacing
    {
        get => _verticalSpacing;
        init => _verticalSpacing = ResponsiveValidation.NonNegativeFinite(value, nameof(VerticalSpacing));
    }

    /// <summary>宽模式（横向，即 HStack 语义）的交叉轴（垂直）对齐；默认 <see cref="Alignment.Center"/>。</summary>
    public Alignment WideAlignment
    {
        get => _wideAlignment;
        init => _wideAlignment = ResponsiveValidation.DefinedEnum(value, nameof(WideAlignment));
    }

    /// <summary>窄模式（纵向，即 VStack 语义）的交叉轴（水平）对齐；默认 <see cref="Alignment.Stretch"/>。</summary>
    public Alignment NarrowAlignment
    {
        get => _narrowAlignment;
        init => _narrowAlignment = ResponsiveValidation.DefinedEnum(value, nameof(NarrowAlignment));
    }

    /// <inheritdoc />
    public override string DebugName => $"AdaptiveStack({Breakpoint:0.##})";

    internal override IReadOnlyList<View> Children => Items;

    /// <summary>按父级约束判断是否使用横向（宽）模式；无界宽度为横向。</summary>
    internal bool IsHorizontal(LayoutConstraints constraints)
        => !constraints.HasBoundedWidth || constraints.MaxWidth >= Breakpoint;

    /// <summary>返回设置宽模式（横向）间距的副本。</summary>
    public AdaptiveStack WithSpacing(double spacing)
        => new(Breakpoint, Items)
        {
            Spacing = spacing,
            VerticalSpacing = VerticalSpacing,
            WideAlignment = WideAlignment,
            NarrowAlignment = NarrowAlignment,
        };

    /// <summary>返回设置窄模式（纵向）间距的副本。</summary>
    public AdaptiveStack WithVerticalSpacing(double verticalSpacing)
        => new(Breakpoint, Items)
        {
            Spacing = Spacing,
            VerticalSpacing = verticalSpacing,
            WideAlignment = WideAlignment,
            NarrowAlignment = NarrowAlignment,
        };

    /// <summary>返回设置窄模式（纵向）交叉轴对齐的副本。</summary>
    public AdaptiveStack Align(Alignment alignment)
        => new(Breakpoint, Items)
        {
            Spacing = Spacing,
            VerticalSpacing = VerticalSpacing,
            WideAlignment = WideAlignment,
            NarrowAlignment = alignment,
        };

    /// <summary>返回设置宽模式（横向）交叉轴对齐的副本。</summary>
    public AdaptiveStack WideAlign(Alignment alignment)
        => new(Breakpoint, Items)
        {
            Spacing = Spacing,
            VerticalSpacing = VerticalSpacing,
            WideAlignment = alignment,
            NarrowAlignment = NarrowAlignment,
        };

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is AdaptiveStack stack
            && stack.Breakpoint == Breakpoint
            && stack.Spacing == Spacing
            && stack.VerticalSpacing == VerticalSpacing
            && stack.WideAlignment == WideAlignment
            && stack.NarrowAlignment == NarrowAlignment;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var horizontal = IsHorizontal(constraints);
        node.AdaptiveIsHorizontal = horizontal;
        StackLayout.Measure(
            node,
            constraints,
            context,
            horizontal ? StackOrientation.Horizontal : StackOrientation.Vertical,
            horizontal ? Spacing : VerticalSpacing,
            horizontal ? WideAlignment : NarrowAlignment);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        // 必须沿用测量阶段选择的模式：父级对齐可能让最终宽度小于测量宽度，不能据此翻转模式。
        var horizontal = node.AdaptiveIsHorizontal
            ?? (!double.IsPositiveInfinity(bounds.Width) && bounds.Width >= Breakpoint);
        node.Frame = bounds;
        StackLayout.Arrange(
            node,
            bounds,
            context,
            horizontal ? StackOrientation.Horizontal : StackOrientation.Vertical,
            horizontal ? Spacing : VerticalSpacing,
            horizontal ? WideAlignment : NarrowAlignment);
    }
}

/// <summary>链式扩展：<c>new AdaptiveStack(560, ...).Spacing(12).VerticalSpacing(8)</c>。</summary>
public static class AdaptiveStackExtensions
{
    /// <summary>设置宽模式（横向）间距。</summary>
    public static AdaptiveStack Spacing(this AdaptiveStack stack, double spacing)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.WithSpacing(spacing);
    }

    /// <summary>设置窄模式（纵向）间距。</summary>
    public static AdaptiveStack VerticalSpacing(this AdaptiveStack stack, double verticalSpacing)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.WithVerticalSpacing(verticalSpacing);
    }
}
