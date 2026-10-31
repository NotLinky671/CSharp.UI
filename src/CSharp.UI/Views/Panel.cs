namespace CSharp.UI;

/// <summary>
/// 单子装饰容器：内边距、背景色、边框与圆角合在一个控件上，
/// 取代 <c>Frame + Padding + Background + Border</c> 的手工拼装。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ContentPadding"/> 与 <see cref="BorderThickness"/> 都参与布局：
/// 子内容在边框与内边距内侧测量/排列（边框是真正占位的描边，不是 <see cref="BorderView"/>
/// 那种零占位覆盖）——无边框用 <c>BorderThickness = 0</c> 表达，没有 ShowBorder 开关。
/// </para>
/// <para>
/// <see cref="BackgroundColor"/>、<see cref="BorderColor"/>、<see cref="CornerRadius"/> 为
/// paint-only：变更只重绘，不触发重新布局。圆角是装饰语义——当前渲染只有矩形裁剪，
/// <c>ContentPadding = 0</c> 时子内容可以覆盖圆角像素，与显式 <see cref="BorderView"/>/
/// <see cref="CornerRadiusView"/> 修饰符同一边界；父级 clip 与显式 <c>.Clip()</c> 正常生效。
/// </para>
/// <para>
/// <see cref="BackgroundColor"/>/<see cref="BorderColor"/> 为 null 时每轮读取当前主题的
/// <see cref="ResolvedTheme.Surface"/>/<see cref="ResolvedTheme.Border"/>。
/// </para>
/// </remarks>
public sealed class Panel : View
{
    private readonly View[] _children;
    private Thickness _contentPadding = new(12);
    private double _borderThickness = 1;
    private double _cornerRadius = 8;

    /// <summary>由单子内容创建。</summary>
    /// <param name="content">面板内的单个子视图。</param>
    public Panel(View content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        _children = [content];
    }

    /// <summary>面板内的单个子视图（只读）。</summary>
    public View Content { get; }

    /// <summary>内容内边距（逻辑像素，默认四边 12）；参与布局，必须有限非负。</summary>
    public Thickness ContentPadding
    {
        get => _contentPadding;
        init
        {
            RequireFiniteNonNegative(value.Left, nameof(ContentPadding));
            RequireFiniteNonNegative(value.Top, nameof(ContentPadding));
            RequireFiniteNonNegative(value.Right, nameof(ContentPadding));
            RequireFiniteNonNegative(value.Bottom, nameof(ContentPadding));
            _contentPadding = value;
        }
    }

    /// <summary>背景色；null 时按当前主题的 <see cref="ResolvedTheme.Surface"/> 每轮读现值。</summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>边框色；null 时按当前主题的 <see cref="ResolvedTheme.Border"/> 每轮读现值。</summary>
    public Color? BorderColor { get; init; }

    /// <summary>边框宽度（逻辑像素，默认 1；0 表示无边框不占位不绘制）；参与布局，必须有限非负。</summary>
    public double BorderThickness
    {
        get => _borderThickness;
        init
        {
            RequireFiniteNonNegative(value, nameof(BorderThickness));
            _borderThickness = value;
        }
    }

    /// <summary>圆角半径（逻辑像素，默认 8）；只影响绘制，必须有限非负。</summary>
    public double CornerRadius
    {
        get => _cornerRadius;
        init
        {
            RequireFiniteNonNegative(value, nameof(CornerRadius));
            _cornerRadius = value;
        }
    }

    /// <inheritdoc />
    public override string DebugName => "Panel";

    internal override IReadOnlyList<View> Children => _children;

    private Thickness Inset => new(
        ContentPadding.Left + BorderThickness,
        ContentPadding.Top + BorderThickness,
        ContentPadding.Right + BorderThickness,
        ContentPadding.Bottom + BorderThickness);

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        var inset = Inset;
        context.Measure(child, constraints.Deflate(inset));
        node.DesiredSize = new Size(
            child.DesiredSize.Width + inset.Horizontal,
            child.DesiredSize.Height + inset.Vertical);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        context.Arrange(node.ChildList[0], bounds.Deflate(Inset));
    }

    // 与 PaddingView 同一模式：内缩量叠加到内容的尺寸声明上。
    internal override LayoutFeatures GetLayoutFeatures(ViewNode node, LayoutAxis mainAxis)
    {
        var inner = node.ChildList.Count > 0
            ? node.ChildList[0].GetLayoutFeatures(mainAxis)
            : LayoutFeatures.FromNatural(node.DesiredSize);
        var inset = Inset;
        return new LayoutFeatures(
            inner.Horizontal.Inflate(inset.Horizontal),
            inner.Vertical.Inflate(inset.Vertical));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node) || node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var radius = CornerRadius;
        var opacity = node.Environment.Opacity;

        var background = BackgroundColor ?? theme.Surface;
        if (background.A > 0)
        {
            context.Commands.Add(new RectangleCommand(node.Frame, background, radius, null, 0, opacity));
        }

        foreach (var child in node.ChildList)
        {
            context.EmitChild(child);
        }

        var border = BorderColor ?? theme.Border;
        if (BorderThickness > 0 && border.A > 0)
        {
            context.Commands.Add(new RectangleCommand(
                node.Frame, Color.Transparent, radius, border, BorderThickness, opacity));
        }
    }

    // 参与布局的只有内缩两项：padding/border 变化必须 re-layout；颜色/圆角 paint-only。
    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Panel panel
            && panel.ContentPadding == ContentPadding
            && panel.BorderThickness == BorderThickness;

    private static void RequireFiniteNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} 的每一边必须是有限非负值。");
        }
    }
}
