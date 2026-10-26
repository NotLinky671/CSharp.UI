namespace CSharp.UI.Markdown;

/// <summary>
/// Markdown → 视图树的视觉样式选项。所有颜色槽位 null → 沿用主题环境（不硬编码色值）。
/// </summary>
public sealed record MarkdownStyleOptions
{
    /// <summary>标题字号档位（索引 0..5 对应 h1..h6）。</summary>
    public IReadOnlyList<double> HeadingSizes { get; init; } = [28, 24, 20, 18, 16, 14];

    /// <summary>代码字体默认：跨平台等宽回退链（Cascadia→Consolas→Menlo→monospace）。</summary>
    public static readonly FontFamily DefaultCodeFontFamily =
        FontFamily.FromName("Cascadia Mono").WithFallbacks("Consolas", "Menlo", "monospace");

    /// <summary>代码（行内与围栏块）字体族；null → <see cref="DefaultCodeFontFamily"/>（等宽回退链）。</summary>
    public FontFamily? CodeFontFamily { get; init; }

    /// <summary>代码块（行内与围栏块）背景色；默认中性半透明灰，两主题可读。显式 <c>null</c> → 无背景。</summary>
    public Color? CodeBackground { get; init; } = new Color(128, 128, 128, 40);

    /// <summary>代码块内边距。</summary>
    public double CodePadding { get; init; } = 8;

    /// <summary>无序列表 marker 字符。</summary>
    public string UnorderedMarker { get; init; } = "•";

    /// <summary>列表 marker 与内容间距。</summary>
    public double ListMarkerGap { get; init; } = 8;

    /// <summary>引用块左侧边条宽。</summary>
    public double QuoteBarWidth { get; init; } = 3;

    /// <summary>引用块左侧边条色；null → Emit 时解 <see cref="Theme.Border"/>（主题感知）。</summary>
    public Color? QuoteBarColor { get; init; }

    /// <summary>引用块内容内边距（左）。</summary>
    public double QuotePadding { get; init; } = 12;

    /// <summary>表格 cell 单侧水平内边距。</summary>
    public double CellPadding { get; init; } = 8;

    /// <summary>分隔线（thematic break）颜色；null → Emit 时解 <see cref="Theme.Border"/>（主题感知）。</summary>
    public Color? ThematicBreakColor { get; init; }

    /// <summary>分隔线高度。</summary>
    public double ThematicBreakHeight { get; init; } = 1;

    /// <summary>链接前景色；null → 继承段落 <see cref="EnvironmentValues.ResolvedForeground"/>（默认即段落前景）。</summary>
    public Color? LinkForeground { get; init; }

    /// <summary>链接悬停 paint 覆盖（paint-only，不触发重排版）。</summary>
    public TextPaintStyle? LinkHoverStyle { get; init; }

    /// <summary>不支持节点的降级样式。</summary>
    public TextPaintStyle? UnsupportedNodeStyle { get; init; } = new() { Foreground = new Color(255, 128, 0) };
}
