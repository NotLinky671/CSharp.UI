namespace CSharp.UI;

/// <summary>
/// 自动换行容器：按声明顺序从左到右贪心分行，行内按 <see cref="LineAlignment"/> 对齐，默认顶部对齐。
/// </summary>
/// <remarks>
/// <para>
/// 行宽来自父级提供的逻辑宽度（有界时按该宽度分行并保留小数）。宽度无界时所有项目排成单行、使用自然尺寸。
/// 行高由该行最终测量后的最高项目决定，整体高度按行间距累加，因此可直接作为纵向 <see cref="ScrollView"/> 的内容。
/// </para>
/// <para>
/// 分行只改变子节点的 <see cref="ViewNode.Frame"/>，不创建“每行运行时父节点”：子项顺序、<c>Key</c> 身份、
/// 焦点、输入状态与 Tab 顺序都按原始声明顺序保持。
/// </para>
/// <para>
/// 第一版不做逐行 Flex 权重分配：直接子项的 <c>.Flex()</c> 权重会给出可定位诊断；<c>.FillWidth()</c> 作为
/// “占整行”的请求处理（独占一行，不参与逐行权重分配）。不可压缩的硬固定项目独占一行并报告溢出；
/// 可重新测量的普通超宽内容按整行宽度重新测量。
/// </para>
/// </remarks>
public sealed class Flow : View
{
    private readonly double _spacing;
    private readonly double _lineSpacing;
    private readonly Alignment _lineAlignment = Alignment.Start;

    /// <summary>由子项创建。</summary>
    public Flow(params View[] children)
        : this((IReadOnlyList<View>)children)
    {
    }

    /// <summary>由子项集合创建。</summary>
    public Flow(IReadOnlyList<View> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Items = children;
    }

    /// <summary>子项（按声明顺序，也是分行与绘制顺序）。</summary>
    public IReadOnlyList<View> Items { get; init; }

    /// <summary>行内项目间距（逻辑像素）；必须有限且非负。</summary>
    public double Spacing
    {
        get => _spacing;
        init => _spacing = ResponsiveValidation.NonNegativeFinite(value, nameof(Spacing));
    }

    /// <summary>行与行之间的间距（逻辑像素）；必须有限且非负。</summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        init => _lineSpacing = ResponsiveValidation.NonNegativeFinite(value, nameof(LineSpacing));
    }

    /// <summary>行内交叉轴（垂直）对齐；默认 <see cref="Alignment.Start"/>（顶部）。</summary>
    public Alignment LineAlignment
    {
        get => _lineAlignment;
        init => _lineAlignment = ResponsiveValidation.DefinedEnum(value, nameof(LineAlignment));
    }

    /// <inheritdoc />
    public override string DebugName => $"Flow({Items.Count})";

    internal override IReadOnlyList<View> Children => Items;

    /// <summary>返回设置行内间距的副本。</summary>
    public Flow WithSpacing(double spacing)
        => new(Items) { Spacing = spacing, LineSpacing = LineSpacing, LineAlignment = LineAlignment };

    /// <summary>返回设置行间距的副本。</summary>
    public Flow WithLineSpacing(double lineSpacing)
        => new(Items) { Spacing = Spacing, LineSpacing = lineSpacing, LineAlignment = LineAlignment };

    /// <summary>返回设置行内对齐的副本。</summary>
    public Flow Align(Alignment alignment)
        => new(Items) { Spacing = Spacing, LineSpacing = LineSpacing, LineAlignment = alignment };

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Flow flow
            && flow.Spacing == Spacing
            && flow.LineSpacing == LineSpacing
            && flow.LineAlignment == LineAlignment;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        => FlowLayout.Measure(node, constraints, context, this);

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
        => FlowLayout.Arrange(node, bounds, context, this);
}

/// <summary>链式扩展：<c>new Flow(...).Spacing(8).LineSpacing(8)</c>。</summary>
public static class FlowExtensions
{
    /// <summary>设置行内项目间距。</summary>
    public static Flow Spacing(this Flow flow, double spacing)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.WithSpacing(spacing);
    }

    /// <summary>设置行与行之间的间距。</summary>
    public static Flow LineSpacing(this Flow flow, double lineSpacing)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.WithLineSpacing(lineSpacing);
    }
}

/// <summary>一个 Flow 子项在一次测量中的最终主轴/交叉轴尺寸快照。</summary>
/// <param name="Main">主轴（水平）尺寸。</param>
/// <param name="Cross">交叉轴（垂直）尺寸。</param>
/// <param name="Fill">是否为 <c>FillWidth</c>（独占整行）。</param>
/// <param name="FillMin">填充时的下限（来自硬声明）。</param>
/// <param name="FillMax">填充时的上限（来自硬声明）。</param>
internal readonly record struct FlowItemLayout(double Main, double Cross, bool Fill, double FillMin, double FillMax);

/// <summary>
/// Flow 一次测量的分行缓存：行划分与每项的最终尺寸。作为节点私有的有界缓存保存，
/// 排列阶段在宽度一致时直接复用，探测作用域退出时随其它测量状态一起恢复。
/// </summary>
internal sealed class FlowLayoutCache
{
    /// <summary>测量时的可用行宽（无界时为 <see cref="double.PositiveInfinity"/>）。</summary>
    public required double Available { get; init; }

    /// <summary>测量时宽度是否有界。</summary>
    public required bool Bounded { get; init; }

    /// <summary>每项的最终尺寸（与 <see cref="ViewNode.ChildList"/> 对齐）。</summary>
    public required FlowItemLayout[] Items { get; init; }

    /// <summary>分行结果：每行是子项下标数组。</summary>
    public required int[][] Lines { get; init; }

    /// <summary>测量得到的内容宽度（各行主轴长度的最大值）。</summary>
    public required double ContentWidth { get; init; }

    /// <summary>测量得到的内容总高度（行高与行间距之和）。</summary>
    public required double TotalHeight { get; init; }
}

/// <summary>Flow 的共享测量/排列实现。</summary>
internal static class FlowLayout
{
    private const double Epsilon = 0.0001;

    /// <summary>测量 Flow：贪心分行、按行汇总高度，并缓存分行结果供排列复用。</summary>
    public static void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context, Flow flow)
    {
        var bounded = constraints.HasBoundedWidth;
        var available = bounded ? Math.Max(0, constraints.MaxWidth) : double.PositiveInfinity;
        var children = node.ChildList;
        var childConstraints = new LayoutConstraints(
            0,
            bounded ? available : double.PositiveInfinity,
            0,
            constraints.MaxHeight);

        var items = new FlowItemLayout[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            context.Measure(child, childConstraints);
            var features = child.GetLayoutFeatures(LayoutAxis.Horizontal).Horizontal;

            // 第一版不做逐行弹性权重：直接子项的 Flex 权重给出可定位诊断，推荐内嵌 HStack/AdaptiveStack。
            if (features.HasExplicitFlex && !features.Fill)
            {
                context.ReportUnsupported(
                    child,
                    LayoutAxis.Horizontal,
                    $"Flow 不支持直接子项的 Flex 权重分配：节点 {child.DebugPath}。" +
                    "请把弹性表单项放入内嵌 HStack/AdaptiveStack，或改用 FillWidth（占整行）。");
            }

            var main = Sanitize(child.DesiredSize.Width);
            var cross = Sanitize(child.DesiredSize.Height);
            var hard = Math.Max(features.ExplicitSize ?? 0, features.HardMinSize ?? 0);
            var declaredMin = Math.Max(hard, features.ContentMinSize ?? 0);

            if (features.Fill)
            {
                if (bounded)
                {
                    main = LayoutFeatureMath.ClampSafe(available, features.EffectiveMin, features.EffectiveMax);
                }

                if (bounded && hard > available + Epsilon)
                {
                    context.ReportOverflow(node, LayoutAxis.Horizontal, hard, available);
                }

                items[i] = new FlowItemLayout(main, cross, Fill: true, features.EffectiveMin, features.EffectiveMax);
                continue;
            }

            // 不可压缩的硬固定项（显式固定尺寸/硬最小值超过整行）：独占一行并报告溢出，不静默裁剪。
            if (bounded && hard > available + Epsilon)
            {
                context.ReportOverflow(node, LayoutAxis.Horizontal, declaredMin, available);
                items[i] = new FlowItemLayout(declaredMin, cross, Fill: false, 0, double.PositiveInfinity);
                continue;
            }

            // 可重新测量的普通超宽内容：按整行宽度重新测量后独占一行。
            if (bounded && declaredMin > available + Epsilon)
            {
                context.MeasureFresh(child, new LayoutConstraints(available, available, 0, constraints.MaxHeight));
                main = Sanitize(child.DesiredSize.Width);
                cross = Sanitize(child.DesiredSize.Height);
            }

            items[i] = new FlowItemLayout(main, cross, Fill: false, 0, double.PositiveInfinity);
        }

        var lines = BreakLines(items, available, bounded, flow.Spacing);
        var (contentWidth, totalHeight) = Summarize(items, lines, available, bounded, flow);

        node.FlowCache = new FlowLayoutCache
        {
            Available = available,
            Bounded = bounded,
            Items = items,
            Lines = lines,
            ContentWidth = contentWidth,
            TotalHeight = totalHeight,
        };

        node.DesiredSize = new Size(
            Math.Max(bounded ? Math.Min(available, contentWidth) : contentWidth, constraints.MinWidth),
            Math.Max(totalHeight, constraints.MinHeight));
    }

    /// <summary>排列 Flow：复用测量时的行划分（宽度变化时仅按缓存的项尺寸重新分行，不再触发测量）。</summary>
    public static void Arrange(ViewNode node, Rect bounds, LayoutContext context, Flow flow)
    {
        node.Frame = bounds;

        if (node.FlowCache is not { } cache)
        {
            return;
        }

        var available = Math.Max(0, bounds.Width);
        var lines = cache.Bounded
            && double.IsFinite(cache.Available)
            && Math.Abs(cache.Available - available) > Epsilon
                ? BreakLines(cache.Items, available, bounded: true, flow.Spacing)
                : cache.Lines;

        var y = bounds.Y;
        foreach (var line in lines)
        {
            var lineHeight = 0.0;
            foreach (var index in line)
            {
                lineHeight = Math.Max(lineHeight, cache.Items[index].Cross);
            }

            var x = bounds.X;
            foreach (var index in line)
            {
                var item = cache.Items[index];
                var main = item.Fill
                    ? LayoutFeatureMath.ClampSafe(available, item.FillMin, item.FillMax)
                    : item.Main;
                var cross = flow.LineAlignment == Alignment.Stretch ? lineHeight : item.Cross;
                var offset = flow.LineAlignment switch
                {
                    Alignment.Center => (lineHeight - cross) / 2,
                    Alignment.End => lineHeight - cross,
                    _ => 0,
                };

                context.Arrange(node.ChildList[index], new Rect(
                    Math.Round(x, 3),
                    Math.Round(y + Math.Max(0, offset), 3),
                    Math.Round(Math.Max(0, main), 3),
                    Math.Round(Math.Max(0, cross), 3)));

                x += main + flow.Spacing;
            }

            y += lineHeight + flow.LineSpacing;
        }
    }

    /// <summary>从左到右贪心分行：放不下下一项则换行；正好放满留在同一行；无界宽度为单行。</summary>
    internal static int[][] BreakLines(FlowItemLayout[] items, double available, bool bounded, double spacing)
    {
        var lines = new List<int[]>();
        List<int>? current = null;
        var currentWidth = 0.0;

        void Flush()
        {
            if (current is { Count: > 0 })
            {
                lines.Add([.. current]);
            }

            current = null;
            currentWidth = 0;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];

            // FillWidth 占整行：有界宽度下独占一行；无界宽度时按契约排成单行（使用自然尺寸）。
            if (item.Fill && bounded)
            {
                Flush();
                lines.Add([i]);
                continue;
            }

            if (current is { Count: > 0 } && currentWidth + spacing + item.Main > available + Epsilon)
            {
                Flush();
            }

            current ??= [];
            currentWidth = current.Count == 0 ? item.Main : currentWidth + spacing + item.Main;
            current.Add(i);
        }

        Flush();
        return [.. lines];
    }

    private static (double ContentWidth, double TotalHeight) Summarize(
        FlowItemLayout[] items,
        int[][] lines,
        double available,
        bool bounded,
        Flow flow)
    {
        var contentWidth = 0.0;
        var totalHeight = 0.0;

        for (var l = 0; l < lines.Length; l++)
        {
            var line = lines[l];
            var lineWidth = 0.0;
            var lineHeight = 0.0;
            for (var k = 0; k < line.Length; k++)
            {
                var item = items[line[k]];
                lineWidth += k == 0 ? item.Main : flow.Spacing + item.Main;
                lineHeight = Math.Max(lineHeight, item.Cross);
            }

            contentWidth = Math.Max(contentWidth, lineWidth);
            totalHeight += lineHeight;
            if (l > 0)
            {
                totalHeight += flow.LineSpacing;
            }
        }

        return (contentWidth, totalHeight);
    }

    private static double Sanitize(double value)
        => double.IsFinite(value) && value > 0 ? value : 0;
}
