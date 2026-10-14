namespace CSharp.UI;

/// <summary>
/// 文本视图：支持中英文混排、换行、对齐与省略。
/// </summary>
/// <remarks>
/// 字体优先使用 <see cref="Font"/>；未显式设置时继承环境字体（例如父级 <c>.Font(...)</c> 或应用默认字体）。
/// 颜色由环境前景色决定（<c>.Foreground(...)</c> 修饰符或主题）。
/// </remarks>
public sealed class Text : View
{
    /// <summary>由文本内容创建。</summary>
    public Text(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>文本内容。修改内容会使布局失效。</summary>
    public string Content { get; init; }

    /// <summary>显式字体；null 表示继承环境字体。</summary>
    public FontDescription? Font { get; init; }

    /// <summary>水平对齐（在排列矩形内）。</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>换行方式。</summary>
    public TextWrapping Wrapping { get; init; } = TextWrapping.NoWrap;

    /// <summary>超出时的省略方式。</summary>
    public TextTrimming Trimming { get; init; } = TextTrimming.None;

    /// <summary>最大行数；仅在换行时有意义。</summary>
    public int MaxLines { get; init; } = int.MaxValue;

    /// <summary>行高（逻辑像素）；0 表示字体默认行高。</summary>
    public double LineHeight { get; init; }

    /// <summary>字距（逻辑像素）。</summary>
    public double LetterSpacing { get; init; }

    /// <inheritdoc />
    public override string DebugName => $"Text(\"{Ellipsis(Content, 24)}\")";

    /// <summary>返回设置了字体的副本。</summary>
    public Text WithFont(FontDescription font) => Copy(font: font);

    /// <summary>返回开启换行的副本。</summary>
    public Text Wrap(int maxLines = int.MaxValue) => Copy(wrapping: TextWrapping.Wrap, maxLines: maxLines);

    /// <summary>返回指定对齐的副本。</summary>
    public Text Align(TextAlignment alignment) => Copy(alignment: alignment);

    /// <summary>返回末行省略的副本（单行）。</summary>
    public Text Ellipsis() => Copy(trimming: TextTrimming.Ellipsis, maxLines: 1);

    /// <summary>返回指定行高的副本。</summary>
    public Text WithLineHeight(double lineHeight) => Copy(lineHeight: lineHeight);

    /// <summary>返回指定字距的副本。</summary>
    public Text WithLetterSpacing(double letterSpacing) => Copy(letterSpacing: letterSpacing);

    private Text Copy(
        string? content = null,
        FontDescription? font = null,
        TextAlignment? alignment = null,
        TextWrapping? wrapping = null,
        TextTrimming? trimming = null,
        int? maxLines = null,
        double? lineHeight = null,
        double? letterSpacing = null)
        => new(content ?? Content)
        {
            Font = font ?? Font,
            Alignment = alignment ?? Alignment,
            Wrapping = wrapping ?? Wrapping,
            Trimming = trimming ?? Trimming,
            MaxLines = maxLines ?? MaxLines,
            LineHeight = lineHeight ?? LineHeight,
            LetterSpacing = letterSpacing ?? LetterSpacing,
        };

    internal static string Ellipsis(string value, int max)
        => value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Text text
            && text.Content == Content
            && Equals(text.Font, Font)
            && text.Alignment == Alignment
            && text.Wrapping == Wrapping
            && text.Trimming == Trimming
            && text.MaxLines == MaxLines
            && text.LineHeight == LineHeight
            && text.LetterSpacing == LetterSpacing;

    /// <summary>构造文本排版请求（字体、宽度、对齐、省略与 DPI）。</summary>
    internal TextLayoutRequest CreateRequest(ViewNode node, LayoutConstraints constraints, double pixelScale)
    {
        var font = Font ?? node.Environment.Font ?? FontDescription.Default;
        var wrapping = Wrapping;
        return new TextLayoutRequest(Content)
        {
            Font = font,
            MaxWidth = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth) : double.PositiveInfinity,
            MaxLines = wrapping == TextWrapping.NoWrap ? Math.Min(MaxLines, 1) : MaxLines,
            Wrapping = wrapping,
            Trimming = Trimming,
            Alignment = Alignment,
            LineHeight = LineHeight,
            LetterSpacing = LetterSpacing,
            PixelScale = pixelScale,
        };
    }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var layout = context.TextService.Layout(CreateRequest(node, constraints, context.PixelScale));
        node.ViewCache = layout;
        node.DesiredSize = layout.Size;
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node))
        {
            return;
        }

        if (node.ViewCache is not TextLayoutResult layout)
        {
            return;
        }

        var textureService = context.TextureService;
        if (textureService is null || layout.Size.IsEmpty)
        {
            return;
        }

        var frame = node.Frame;
        var x = Alignment switch
        {
            TextAlignment.Center => frame.X + ((frame.Width - layout.Size.Width) / 2),
            TextAlignment.Right => frame.Right - layout.Size.Width,
            _ => frame.X,
        };
        var y = frame.Y + ((frame.Height - layout.Size.Height) / 2);
        var bounds = new Rect(Math.Round(x, 3), Math.Round(y, 3), layout.Size.Width, layout.Size.Height);

        var key = new TextTextureKey(
            layout.Text,
            layout.Font,
            CreateRequest(node, node.LastConstraints, context.PixelScale).MaxWidth,
            MaxLines,
            Wrapping,
            Trimming,
            Alignment,
            LineHeight,
            context.PixelScale,
            LetterSpacing);

        var handle = textureService.GetOrCreate(key, () => context.TextService.Rasterize(layout));
        if (!handle.IsValid)
        {
            return;
        }

        var color = node.Environment.ResolvedForeground;
        context.Commands.Add(new TextureCommand(
            handle,
            bounds,
            new Rect(0, 0, handle.PixelWidth, handle.PixelHeight),
            color,
            node.Environment.Opacity));
    }
}

/// <summary>文本纹理缓存键：包含一切影响栅格化结果的输入。</summary>
internal readonly record struct TextTextureKey(
    string Text,
    FontDescription Font,
    double MaxWidth,
    int MaxLines,
    TextWrapping Wrapping,
    TextTrimming Trimming,
    TextAlignment Alignment,
    double LineHeight,
    double PixelScale,
    double LetterSpacing);

/// <summary>纯色矩形视图；可作为背景、分隔线或色块。</summary>
/// <remarks>
/// <see cref="Rectangle"/> 未指定尺寸时默认测量为 0；在 <see cref="ZStack"/>（Stretch 对齐）或
/// <c>Frame</c> 中会被拉伸到容器大小。
/// </remarks>
public sealed class Rectangle : View
{
    /// <summary>由填充色创建。</summary>
    public Rectangle(Color fill)
    {
        Fill = fill;
    }

    /// <summary>填充色。</summary>
    public Color Fill { get; init; }

    /// <summary>圆角半径。</summary>
    public double CornerRadius { get; init; }

    /// <summary>描边色；null 表示无边框。</summary>
    public Color? Stroke { get; init; }

    /// <summary>描边宽度。</summary>
    public double StrokeWidth { get; init; }

    /// <summary>显式尺寸；null 表示由约束决定（ZStack/Frame 拉伸）。</summary>
    public Size? FixedSize { get; init; }

    /// <inheritdoc />
    public override string DebugName => $"Rectangle({Fill})";

    /// <summary>返回设置圆角的副本。</summary>
    public Rectangle WithCornerRadius(double radius) => new(Fill)
    {
        CornerRadius = radius,
        Stroke = Stroke,
        StrokeWidth = StrokeWidth,
        FixedSize = FixedSize,
    };

    /// <summary>返回设置描边的副本。</summary>
    public Rectangle WithStroke(Color color, double width = 1) => new(Fill)
    {
        CornerRadius = CornerRadius,
        Stroke = color,
        StrokeWidth = width,
        FixedSize = FixedSize,
    };

    /// <summary>返回设置固定尺寸的副本。</summary>
    public Rectangle Size(double width, double height) => new(Fill)
    {
        CornerRadius = CornerRadius,
        Stroke = Stroke,
        StrokeWidth = StrokeWidth,
        FixedSize = new Size(width, height),
    };

    // 填充色只影响绘制；尺寸、圆角与描边影响布局/几何。
    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Rectangle rectangle
            && rectangle.FixedSize == FixedSize
            && rectangle.CornerRadius == CornerRadius
            && rectangle.StrokeWidth == StrokeWidth;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var desired = FixedSize ?? new Size(
            constraints.IsWidthTight ? constraints.MaxWidth : Math.Max(0, constraints.MinWidth),
            constraints.IsHeightTight ? constraints.MaxHeight : Math.Max(0, constraints.MinHeight));
        node.DesiredSize = desired;
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node) || node.Frame.IsEmpty)
        {
            return;
        }

        var radius = CornerRadius > 0 ? CornerRadius : node.Environment.CornerRadius;
        context.Commands.Add(new RectangleCommand(
            node.Frame,
            Fill,
            radius,
            Stroke,
            StrokeWidth,
            node.Environment.Opacity));
    }
}

/// <summary>
/// 弹性空白：默认吸收堆叠容器主轴上的剩余空间；<c>new Spacer(8)</c> 表示固定 8 逻辑像素的空隙。
/// </summary>
public sealed class Spacer : View
{
    /// <summary>创建弹性空白。</summary>
    public Spacer()
    {
    }

    /// <summary>创建固定大小的空白。</summary>
    public Spacer(double fixedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fixedSize);
        FixedSize = fixedSize;
    }

    /// <summary>固定尺寸；null 表示弹性。</summary>
    public double? FixedSize { get; init; }

    /// <summary>弹性权重（固定尺寸时为 0）。</summary>
    public double Flex { get; init; } = 1;

    /// <inheritdoc />
    public override string DebugName => FixedSize is { } size ? $"Spacer({size})" : "Spacer";

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Spacer spacer && spacer.FixedSize == FixedSize && spacer.Flex == Flex;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        node.DesiredSize = FixedSize is { } size
            ? new Size(size, size)
            : Size.Zero;
    }

    internal override double FlexGrow(ViewNode node, bool horizontal)
        => FixedSize is null ? Math.Max(0, Flex) : 0;
}
