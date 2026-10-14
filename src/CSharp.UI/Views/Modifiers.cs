namespace CSharp.UI;

/// <summary>
/// 单子项修饰符视图的基类：默认不改变布局，只把测量/排列/绘制透传给子项。
/// </summary>
public abstract class ModifierView : View
{
    private readonly View[] _children;

    /// <summary>由被修饰的视图创建。</summary>
    protected ModifierView(View content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        _children = [content];
    }

    /// <summary>被修饰的视图。</summary>
    public View Content { get; }

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        context.Measure(child, constraints);
        node.DesiredSize = child.DesiredSize;
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        context.Arrange(node.ChildList[0], bounds);
    }
}

/// <summary>内边距修饰符：<c>view.Padding(24)</c>。</summary>
public sealed class PaddingView : ModifierView
{
    /// <summary>由内容与内边距创建。</summary>
    public PaddingView(View content, Thickness padding)
        : base(content)
    {
        Padding = padding;
    }

    /// <summary>内边距。</summary>
    public Thickness Padding { get; }

    /// <inheritdoc />
    public override string DebugName => $"Padding({Padding})";

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        context.Measure(child, constraints.Deflate(Padding));
        node.DesiredSize = new Size(
            child.DesiredSize.Width + Padding.Horizontal,
            child.DesiredSize.Height + Padding.Vertical);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        context.Arrange(node.ChildList[0], bounds.Deflate(Padding));
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is PaddingView padding && padding.Padding == Padding;
}

/// <summary>尺寸约束修饰符：<c>view.Frame(width: 200)</c>。</summary>
public sealed class FrameView : ModifierView
{
    /// <summary>由内容与尺寸约束创建。</summary>
    public FrameView(
        View content,
        double? width = null,
        double? height = null,
        double? minWidth = null,
        double? minHeight = null,
        double? maxWidth = null,
        double? maxHeight = null,
        Alignment alignment = Alignment.Center)
        : base(content)
    {
        Width = width;
        Height = height;
        MinWidth = minWidth;
        MinHeight = minHeight;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        Alignment = alignment;
    }

    /// <summary>固定宽度。</summary>
    public double? Width { get; }

    /// <summary>固定高度。</summary>
    public double? Height { get; }

    /// <summary>最小宽度。</summary>
    public double? MinWidth { get; }

    /// <summary>最小高度。</summary>
    public double? MinHeight { get; }

    /// <summary>最大宽度。</summary>
    public double? MaxWidth { get; }

    /// <summary>最大高度。</summary>
    public double? MaxHeight { get; }

    /// <summary>子项在框内的对齐方式。</summary>
    public Alignment Alignment { get; }

    /// <inheritdoc />
    public override string DebugName => $"Frame({Width?.ToString("0.##") ?? "-"}×{Height?.ToString("0.##") ?? "-"})";

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        var childConstraints = constraints;

        if (Width is { } width)
        {
            childConstraints = new LayoutConstraints(width, width, childConstraints.MinHeight, childConstraints.MaxHeight);
        }
        else
        {
            if (MinWidth is { } minWidth)
            {
                childConstraints = childConstraints.WithMin(minWidth, childConstraints.MinHeight);
            }

            if (MaxWidth is { } maxWidth)
            {
                childConstraints = childConstraints.WithMaxWidth(maxWidth);
            }
        }

        if (Height is { } height)
        {
            childConstraints = new LayoutConstraints(childConstraints.MinWidth, childConstraints.MaxWidth, height, height);
        }
        else
        {
            if (MinHeight is { } minHeight)
            {
                childConstraints = childConstraints.WithMin(childConstraints.MinWidth, minHeight);
            }

            if (MaxHeight is { } maxHeight)
            {
                childConstraints = childConstraints.WithMaxHeight(maxHeight);
            }
        }

        context.Measure(child, childConstraints);

        var width2 = Width ?? Math.Max(child.DesiredSize.Width, MinWidth ?? 0);
        var height2 = Height ?? Math.Max(child.DesiredSize.Height, MinHeight ?? 0);
        if (MaxWidth is { } clampWidth)
        {
            width2 = Math.Min(width2, clampWidth);
        }

        if (MaxHeight is { } clampHeight)
        {
            height2 = Math.Min(height2, clampHeight);
        }

        // Frame 只应用自身声明的最小/最大尺寸；父级上限由测量管线统一收缩。
        node.DesiredSize = new Size(
            Math.Clamp(width2, MinWidth ?? 0, MaxWidth ?? double.PositiveInfinity),
            Math.Clamp(height2, MinHeight ?? 0, MaxHeight ?? double.PositiveInfinity));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        var child = node.ChildList[0];
        var rect = Alignment == Alignment.Stretch
            ? bounds
            : bounds.AlignInside(child.DesiredSize, Alignment);
        context.Arrange(child, rect);
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is FrameView frame
            && frame.Width == Width
            && frame.Height == Height
            && frame.MinWidth == MinWidth
            && frame.MinHeight == MinHeight
            && frame.MaxWidth == MaxWidth
            && frame.MaxHeight == MaxHeight
            && frame.Alignment == Alignment;
}

/// <summary>背景色修饰符：在内容之后再绘制一层背景矩形，覆盖内边距区域。</summary>
public sealed class BackgroundView : ModifierView
{
    /// <summary>由内容与背景色创建。</summary>
    public BackgroundView(View content, Color color)
        : base(content)
    {
        Color = color;
    }

    /// <summary>背景色。</summary>
    public Color Color { get; }

    /// <inheritdoc />
    public override string DebugName => $"Background({Color})";

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (context.IsVisible(node) && !node.Frame.IsEmpty && Color.A > 0)
        {
            context.Commands.Add(new RectangleCommand(
                node.Frame,
                Color,
                node.Environment.CornerRadius,
                null,
                0,
                node.Environment.Opacity));
        }

        foreach (var child in node.ChildList)
        {
            context.EmitChild(child);
        }
    }

    internal override bool LayoutPropertiesEquivalent(View other) => other is BackgroundView;
}

/// <summary>前景色修饰符：为子树提供继承的前景色（文本等使用）。</summary>
public sealed class ForegroundView : ModifierView
{
    /// <summary>由内容与前景色创建。</summary>
    public ForegroundView(View content, Color color)
        : base(content)
    {
        Color = color;
    }

    /// <summary>前景色。</summary>
    public Color Color { get; }

    /// <inheritdoc />
    public override string DebugName => $"Foreground({Color})";

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => parent with { Foreground = Color };

    // 前景色通过环境继承给子树，颜色变化必须让子树重新解析环境。
    internal override bool LayoutPropertiesEquivalent(View other) => other is ForegroundView view && view.Color == Color;
}

/// <summary>边框修饰符：在内容之上绘制描边矩形。</summary>
public sealed class BorderView : ModifierView
{
    /// <summary>由内容、颜色与线宽创建。</summary>
    public BorderView(View content, Color color, double width, double cornerRadius)
        : base(content)
    {
        Color = color;
        Width = width;
        CornerRadius = cornerRadius;
    }

    /// <summary>边框颜色。</summary>
    public Color Color { get; }

    /// <summary>边框宽度（逻辑像素）。</summary>
    public double Width { get; }

    /// <summary>边框圆角；0 表示使用环境圆角。</summary>
    public double CornerRadius { get; }

    /// <inheritdoc />
    public override string DebugName => $"Border({Color}, {Width})";

    internal override void Emit(ViewNode node, DrawContext context)
    {
        foreach (var child in node.ChildList)
        {
            context.EmitChild(child);
        }

        if (!context.IsVisible(node) || node.Frame.IsEmpty || Width <= 0 || Color.A == 0)
        {
            return;
        }

        var radius = CornerRadius > 0 ? CornerRadius : node.Environment.CornerRadius;
        context.Commands.Add(new RectangleCommand(
            node.Frame,
            Color.Transparent,
            radius,
            Color,
            Width,
            node.Environment.Opacity));
    }

    internal override bool LayoutPropertiesEquivalent(View other) => other is BorderView;
}

/// <summary>圆角修饰符：为子树的背景/边框/矩形提供继承的圆角半径。</summary>
public sealed class CornerRadiusView : ModifierView
{
    /// <summary>由内容与圆角半径创建。</summary>
    public CornerRadiusView(View content, double radius)
        : base(content)
    {
        Radius = radius;
    }

    /// <summary>圆角半径。</summary>
    public double Radius { get; }

    /// <inheritdoc />
    public override string DebugName => $"CornerRadius({Radius})";

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => parent with { CornerRadius = Radius };

    internal override bool LayoutPropertiesEquivalent(View other) => other is CornerRadiusView view && view.Radius == Radius;
}

/// <summary>透明度修饰符：与父级透明度相乘，影响子树全部绘制命令。</summary>
public sealed class OpacityView : ModifierView
{
    /// <summary>由内容与透明度创建。</summary>
    public OpacityView(View content, double opacity)
        : base(content)
    {
        Opacity = Math.Clamp(opacity, 0, 1);
    }

    /// <summary>透明度（0—1）。</summary>
    public double Opacity { get; }

    /// <inheritdoc />
    public override string DebugName => $"Opacity({Opacity})";

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => parent with { Opacity = Math.Clamp(parent.Opacity * Opacity, 0, 1) };

    internal override bool LayoutPropertiesEquivalent(View other) => other is OpacityView view && view.Opacity == Opacity;
}

/// <summary>矩形裁剪修饰符：把子树裁剪到自身矩形。</summary>
public sealed class ClipView : ModifierView
{
    /// <summary>由内容创建。</summary>
    public ClipView(View content)
        : base(content)
    {
    }

    /// <inheritdoc />
    public override string DebugName => "Clip";

    internal override Rect? GetChildClip(ViewNode node) => node.Frame;

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var restore = context.CurrentClip;
        if (!context.PushClip(node.Frame))
        {
            // 裁剪区为空：不输出任何可见内容。
            return;
        }

        foreach (var child in node.ChildList)
        {
            context.EmitChild(child);
        }

        context.PopClip(restore);
    }

    internal override bool LayoutPropertiesEquivalent(View other) => other is ClipView;
}

/// <summary>禁用修饰符：阻止子树的命中、焦点与键盘处理，并向子树传播禁用状态。</summary>
public sealed class DisabledView : ModifierView
{
    /// <summary>由内容与禁用标记创建。</summary>
    public DisabledView(View content, bool disabled)
        : base(content)
    {
        Disabled = disabled;
    }

    /// <summary>是否禁用。</summary>
    public bool Disabled { get; }

    /// <inheritdoc />
    public override string DebugName => $"Disabled({Disabled})";

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => parent with { Disabled = Disabled || parent.Disabled };

    internal override bool IsHitTestVisible(ViewNode node) => !Disabled;

    // 禁用状态通过环境传播，切换时必须重新解析子树环境。
    internal override bool LayoutPropertiesEquivalent(View other) => other is DisabledView view && view.Disabled == Disabled;
}

/// <summary>主题修饰符：为子树提供主题（颜色继承与环境默认值）。</summary>
public sealed class ThemeView : ModifierView
{
    /// <summary>由内容与主题创建。</summary>
    public ThemeView(View content, Theme theme)
        : base(content)
    {
        Theme = theme;
    }

    /// <summary>主题。</summary>
    public Theme Theme { get; }

    /// <inheritdoc />
    public override string DebugName => $"Theme({(Theme.IsDark ? "dark" : "light")})";

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => parent with { Theme = Theme };

    internal override bool LayoutPropertiesEquivalent(View other) => other is ThemeView view && ReferenceEquals(view.Theme, Theme);
}

/// <summary>字体修饰符：为子树提供继承字体；子级显式 <c>.Font(...)</c> 仍可覆盖。</summary>
public sealed class FontView : ModifierView
{
    /// <summary>由内容与字体创建。</summary>
    public FontView(View content, FontDescription font)
        : base(content)
    {
        Font = font;
    }

    /// <summary>字体。</summary>
    public FontDescription Font { get; }

    /// <inheritdoc />
    public override string DebugName => $"Font({Font})";

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => parent with { Font = Font };

    // 字体影响文本测量，必须重新布局。
    internal override bool LayoutPropertiesEquivalent(View other) => other is FontView view && Equals(view.Font, Font);
}

/// <summary>链式修饰符扩展。</summary>
public static class Modifiers
{
    /// <summary>四边等量内边距。</summary>
    public static View Padding(this View view, double uniform) => new PaddingView(view, new Thickness(uniform));

    /// <summary>水平与垂直方向内边距。</summary>
    public static View Padding(this View view, double horizontal, double vertical) => new PaddingView(view, new Thickness(horizontal, vertical));

    /// <summary>四边分别指定内边距。</summary>
    public static View Padding(this View view, double left, double top, double right, double bottom)
        => new PaddingView(view, new Thickness(left, top, right, bottom));

    /// <summary>使用 <see cref="Thickness"/> 指定内边距。</summary>
    public static View Padding(this View view, Thickness thickness) => new PaddingView(view, thickness);

    /// <summary>固定尺寸。</summary>
    public static View Frame(this View view, double? width = null, double? height = null, Alignment alignment = Alignment.Center)
        => new FrameView(view, width, height, alignment: alignment);

    /// <summary>尺寸约束（最小/最大与精确值）。</summary>
    public static View Frame(
        this View view,
        double? width,
        double? height,
        double? minWidth,
        double? minHeight,
        double? maxWidth,
        double? maxHeight,
        Alignment alignment = Alignment.Center)
        => new FrameView(view, width, height, minWidth, minHeight, maxWidth, maxHeight, alignment);

    /// <summary>固定宽度。</summary>
    public static View Width(this View view, double width) => new FrameView(view, width, null);

    /// <summary>固定高度。</summary>
    public static View Height(this View view, double height) => new FrameView(view, null, height);

    /// <summary>固定尺寸（等价于 <c>Frame(width, height)</c>）。</summary>
    public static View Size(this View view, double width, double height) => new FrameView(view, width, height);

    /// <summary>背景色。</summary>
    public static View Background(this View view, Color color) => new BackgroundView(view, color);

    /// <summary>子树前景色。</summary>
    public static View Foreground(this View view, Color color) => new ForegroundView(view, color);

    /// <summary>描边边框。</summary>
    public static View Border(this View view, Color color, double width = 1, double cornerRadius = 0)
        => new BorderView(view, color, width, cornerRadius);

    /// <summary>圆角半径（影响背景、边框与矩形）。</summary>
    public static View CornerRadius(this View view, double radius) => new CornerRadiusView(view, radius);

    /// <summary>透明度（0—1）。</summary>
    public static View Opacity(this View view, double opacity) => new OpacityView(view, opacity);

    /// <summary>把子树裁剪到自身矩形。</summary>
    public static View Clip(this View view) => new ClipView(view);

    /// <summary>禁用子树（阻止命中、焦点与键盘处理）。</summary>
    public static View Disabled(this View view, bool disabled = true) => new DisabledView(view, disabled);

    /// <summary>为子树设置主题。</summary>
    public static View Theme(this View view, Theme theme) => new ThemeView(view, theme);

    /// <summary>为子树设置字体。</summary>
    public static View Font(this View view, FontDescription font) => new FontView(view, font);

    /// <summary>为子树设置字体（按家族名与字号）。</summary>
    public static View Font(this View view, string familyName, double size, FontWeight weight = FontWeight.Normal, bool italic = false)
        => new FontView(view, FontDescription.FromName(familyName, size, weight, italic));
}
