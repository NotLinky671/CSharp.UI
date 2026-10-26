using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CSharp.UI;

namespace CSharp.UI.Markdown;

/// <summary>
/// 把 Markdig block 树递归适配为 <see cref="View"/> 树。
/// 只消费 <see cref="MarkdownDocument.Ast"/>，不改 Core/View/ScrollView。
/// </summary>
internal static class BlockAdapter
{
    /// <summary>
    /// 入口：整篇 AST → 根 <see cref="View"/>（外层 <see cref="VStack"/>）。
    /// </summary>
    /// <param name="document">已解析文档。</param>
    /// <param name="view">宿主组件实例（拿样式/策略/回调）。</param>
    public static View BuildDocument(MarkdownDocument document, MarkdownBlockView view)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(view);

        var style = view.Style ?? new MarkdownStyleOptions();
        var linkPolicy = view.LinkPolicy ?? MarkdownLinkPolicy.Default;

        return AdaptSequence(document.Ast, view, style, linkPolicy);
    }

    // —— 块序列装配（margin-collapse）——
    /// <summary>
    /// 块序列 → 容器 <see cref="View"/>：相邻块间按 margin-collapse 插固定
    /// <see cref="Spacer"/>（gap = max(前块 after, 后块 before)）；首块不产前空白。
    /// cell 内单块适配另有 cell 级后处理（对齐/header bold），经 <paramref name="postProcess"/> 钩子注入。
    /// </summary>
    private static View AdaptSequence(IEnumerable<Block> blocks, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy, Func<View, View>? postProcess = null)
    {
        var children = new List<View>();
        var prevAfter = 0.0;
        var hasPrev = false;

        foreach (var block in blocks)
        {
            var v = AdaptBlock(block, view, style, linkPolicy);
            if (v is null)
            {
                continue;
            }

            if (hasPrev)
            {
                var gap = Math.Max(prevAfter, BlockBefore(block, style));
                if (gap > 0)
                {
                    children.Add(new Spacer(gap));
                }
            }

            children.Add(postProcess is { } pp ? pp(v) : v);
            prevAfter = BlockAfter(block, style);
            hasPrev = true;
        }
        return children.Count == 1 ? children[0] : new VStack(children.ToArray());
    }

    // —— 块级映射 ——

    /// <summary>块前间距：仅标题非零（节间分隔）；其余块靠前一块的 after。</summary>
    private static double BlockBefore(Block block, MarkdownStyleOptions style)
        => block is HeadingBlock ? Math.Max(0, style.HeadingSpacingBefore) : 0;

    /// <summary>块后间距：标题 <see cref="MarkdownStyleOptions.HeadingSpacingAfter"/>，其余 <see cref="MarkdownStyleOptions.BlockSpacing"/>。</summary>
    private static double BlockAfter(Block block, MarkdownStyleOptions style)
        => block is HeadingBlock ? Math.Max(0, style.HeadingSpacingAfter) : Math.Max(0, style.BlockSpacing);

    private static View? AdaptBlock(Block block, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        return block switch
        {
            HeadingBlock heading => AdaptHeading(heading, view, style, linkPolicy),
            ParagraphBlock para => AdaptParagraph(para, view, style, linkPolicy),
            ListBlock list => AdaptList(list, view, style, linkPolicy),
            QuoteBlock quote => AdaptQuote(quote, view, style, linkPolicy),
            ThematicBreakBlock => new ThemedBar { Fill = style.ThematicBreakColor, FixedSize = new Size(0, style.ThematicBreakHeight) },
            FencedCodeBlock fenced => AdaptCodeBlock(fenced, style),
            CodeBlock code => AdaptCodeBlock(code, style),
            HtmlBlock html => AdaptHtmlBlock(html, view, style, linkPolicy),
            Table table => AdaptTable(table, view, style, linkPolicy),
            _ => AdaptUnknownBlock(block, view, style, linkPolicy),
        };
    }

    private static View AdaptHeading(HeadingBlock heading, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var size = style.HeadingSizes.Count >= level ? style.HeadingSizes[level - 1] : style.HeadingSizes[^1];
        var styled = InlineAdapter.Adapt(heading.Inline, view.Document.Options.Html, linkPolicy, style);
        return new RichText(styled)
        {
            Wrapping = TextWrapping.Wrap,
            Font = new FontDescription(FontFamily.Default, size, FontWeight.Bold, false),
            OnLinkActivate = view.OnLinkActivate is { } a ? o => a(o!) : null,
            OnLinkHover = view.OnLinkHover,
            LinkHoverStyle = style.LinkHoverStyle,
        };
    }

    private static View AdaptParagraph(ParagraphBlock para, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        // 段落唯一 inline image → 块级图（MD-04）；混排走 inline 路径。
        if (MarkdownImageView.TryCreate(para, view, style, out var imageView))
        {
            return imageView!;
        }

        var styled = InlineAdapter.Adapt(para.Inline, view.Document.Options.Html, linkPolicy, style);
        return new RichText(styled)
        {
            Wrapping = TextWrapping.Wrap,
            OnLinkActivate = view.OnLinkActivate is { } a ? o => a(o!) : null,
            OnLinkHover = view.OnLinkHover,
            LinkHoverStyle = style.LinkHoverStyle,
        };
    }

    private static View AdaptList(ListBlock list, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        var items = new List<View>();
        var ordered = list.IsOrdered;
        var number = 1;
        if (ordered && list.OrderedStart is { } start && int.TryParse(start, out var s0))
        {
            number = s0;
        }

        foreach (var item in list)
        {
            if (item is not ListItemBlock li)
            {
                continue;
            }

            var marker = ordered ? $"{number}." : style.UnorderedMarker;
            number++;

            // TaskList 项 → marker 前置 "[ ]"/"[x]"；禁用 TaskLists 时 AST 不含 TaskList 节点。
            var taskPrefix = ExtractTaskListPrefix(li);
            if (taskPrefix is not null)
            {
                marker = taskPrefix + " " + marker;
            }

            // item 内多块（段落/嵌套列表/代码…）走统一 margin-collapse，不双重累积段距。
            var contentView = AdaptSequence(li, view, style, linkPolicy).FillWidth();

            // hanging indent：marker 固定宽列 + content 弹性占满剩余。
            var markerView = new RichText(StyledText.Create(marker)) { Wrapping = TextWrapping.NoWrap };
            var itemView = new HStack(markerView, contentView) { Spacing = style.ListMarkerGap, VerticalAlignment = Alignment.Start };
            items.Add(itemView);
        }

        return new VStack(items.ToArray()) { Spacing = Math.Max(0, style.ListItemSpacing) };
    }

    /// <summary>TaskList 项的 "[ ]"/"[x]" 前缀；非 task 项 → null。</summary>
    private static string? ExtractTaskListPrefix(ListItemBlock item)
    {
        if (item.Count == 0 || item[0] is not ParagraphBlock para || para.Inline is null)
        {
            return null;
        }

        foreach (var inline in para.Inline)
        {
            if (inline is Markdig.Extensions.TaskLists.TaskList taskList)
            {
                return taskList.Checked ? "[x]" : "[ ]";
            }
            break; // TaskList 项必为首个 inline。
        }
        return null;
    }

    private static View AdaptQuote(QuoteBlock quote, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        // 引用内块序列同样按 margin-collapse 间距（首块无顶部空白）。
        var inner = AdaptSequence(quote, view, style, linkPolicy)
            .Padding(left: style.QuotePadding, top: 0, right: 0, bottom: 0)
            .FillWidth();

        var bar = new ThemedBar { Fill = style.QuoteBarColor, FixedSize = new Size(style.QuoteBarWidth, 0) };

        return new HStack(bar, inner) { VerticalAlignment = Alignment.Stretch };
    }

    private static View AdaptCodeBlock(CodeBlock code, MarkdownStyleOptions style)
    {
        var text = code.Lines.ToString();
        // 空代码块：不产 range（TextStyleRange(0,0) 会被 StyledText 拒绝），但保留
        // ScrollView+padding+background 的可见盒。
        var ranges = text.Length == 0
            ? (IReadOnlyList<TextStyleRange>)[]
            : [new TextStyleRange(0, text.Length, new TextStyle
            {
                Family = style.CodeFontFamily ?? MarkdownStyleOptions.DefaultCodeFontFamily,
            })];
        var styled = new StyledText(text, ranges, []);
        View content = new RichText(styled) { Wrapping = TextWrapping.NoWrap }
            .Padding(style.CodePadding);
        if (style.CodeBackground is { } codeBg)
        {
            content = content.Background(codeBg);
        }

        return new ScrollView(content, ScrollAxis.Horizontal);
    }

    private static View? AdaptHtmlBlock(HtmlBlock html, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        var text = html.Lines.ToString();
        return view.Document.Options.Html == HtmlPolicy.Literal
            ? new RichText(StyledText.Create(text))
            : null;
    }

    /// <summary>
    /// TableBlock → <see cref="Responsive"/> + <see cref="ScrollView"/> + <see cref="MarkdownTable"/>。
    /// cell 块序列（margin-collapse + 对齐/header bold 后处理）；header 行 bold；缺 cell 补空；
    /// <see cref="Table.ColumnDefinitions"/> 对齐下到 cell <see cref="RichText.Alignment"/>。
    /// </summary>
    private static View AdaptTable(Table table, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        var rows = new List<IReadOnlyList<View>>();
        var aligns = (table.ColumnDefinitions ?? []).Select(d => d.Alignment).ToList();

        var colIdx = 0;
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;
            var cells = new List<View>();
            colIdx = 0;
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell) continue;
                // 对齐：cell 序号（Markdig TableCell.ColumnIndex 可能为 -1）→ ColumnDefinitions[ci]。
                var ci = cell.ColumnIndex >= 0 ? cell.ColumnIndex : colIdx;
                var isHeader = row.IsHeader;
                cells.Add(AdaptSequence(cell, view, style, linkPolicy,
                    v => PostProcessCellBlock(v, ci, isHeader, aligns)));
                colIdx++;
            }
            rows.Add(cells);
        }

        return new Responsive(info => new ScrollView(
            new MarkdownTable(rows,
                info.IsWidthBounded ? info.AvailableWidth : double.PositiveInfinity,
                style.CellPadding,
                style.CellVerticalPadding),
            ScrollAxis.Horizontal));
    }

    /// <summary>cell 内单个块的适配后处理：<see cref="Table.ColumnDefinitions"/> 对齐下传 <see cref="RichText.Alignment"/>；header 行整条 [0,len) 补 Bold overlay（原 range 保留其他属性，洞补 base bold）。</summary>
    private static View PostProcessCellBlock(View v, int columnIndex, bool isHeader, IReadOnlyList<TableColumnAlign?> aligns)
    {
        if (v is RichText rt && columnIndex < aligns.Count)
        {
            v = new RichText(rt.Content)
            {
                Wrapping = rt.Wrapping,
                Alignment = aligns[columnIndex] switch
                {
                    TableColumnAlign.Center => TextAlignment.Center,
                    TableColumnAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                },
                OnLinkActivate = rt.OnLinkActivate,
                OnLinkHover = rt.OnLinkHover,
                LinkHoverStyle = rt.LinkHoverStyle,
            };
        }

        if (isHeader && v is RichText rich && rich.Content.Text.Length > 0)
        {
            var styled = rich.Content;
            var textLen = styled.Text.Length;
            // 端点集 = {0, len} ∪ 原 range 端点；按端点切片、逐片合 bold（不重叠）。
            var cuts = new SortedSet<int> { 0, textLen };
            foreach (var r in styled.Ranges) { cuts.Add(r.Start); cuts.Add(r.End); }
            var cutList = cuts.ToList();
            var byStart = styled.Ranges.ToDictionary(r => r.Start);
            var ranges = new List<TextStyleRange>(cutList.Count - 1);
            for (var i = 0; i + 1 < cutList.Count; i++)
            {
                var segStart = cutList[i];
                var segEnd = cutList[i + 1];
                // 该切片落在某原 range 内 → 保留原 style + Weight=Bold；洞 → base bold。
                if (byStart.TryGetValue(segStart, out var orig) && orig.End == segEnd)
                {
                    var merged = (orig.Style ?? new TextStyle()) with { Weight = FontWeight.Bold };
                    ranges.Add(new TextStyleRange(segStart, segEnd, merged));
                }
                else
                {
                    ranges.Add(new TextStyleRange(segStart, segEnd, new TextStyle { Weight = FontWeight.Bold }));
                }
            }
            v = new RichText(styled.WithRanges(ranges))
            {
                Wrapping = rich.Wrapping,
                Alignment = rich.Alignment,
                OnLinkActivate = rich.OnLinkActivate,
                OnLinkHover = rich.OnLinkHover,
                LinkHoverStyle = rich.LinkHoverStyle,
            };
        }

        return v;
    }

    private static View AdaptUnknownBlock(Block block, MarkdownBlockView view, MarkdownStyleOptions style, MarkdownLinkPolicy linkPolicy)
    {
        var literal = TryGetBlockLiteral(block);
        return UnknownLiteral(literal ?? $"[{block.GetType().Name}]", style);
    }

    private static View UnknownLiteral(string text, MarkdownStyleOptions style)
    {
        // 空 text：不产空 range（StyledText 验证会拒）；返回 plain 空 RichText。
        if (text.Length == 0)
        {
            return new RichText(StyledText.Create(string.Empty))
            { Wrapping = TextWrapping.Wrap };
        }

        return new RichText(new StyledText(
            text,
            [new TextStyleRange(0, text.Length, style.UnsupportedNodeStyle is { } s
                ? new TextStyle { Foreground = s.Foreground, Background = s.Background, Underline = s.Underline, Strikethrough = s.Strikethrough }
                : new TextStyle())],
            []))
        { Wrapping = TextWrapping.Wrap };
    }

    private static string? TryGetBlockLiteral(Block block)
    {
        return block switch
        {
            LeafBlock leaf when leaf.Inline is { } inline => TryExtractInlineLiteral(inline),
            ContainerBlock container => TryExtractContainerLiteral(container),
            _ => null,
        };
    }

    private static string? TryExtractContainerLiteral(ContainerBlock container)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in container)
        {
            if (child is LeafBlock leaf && leaf.Inline is { } inline)
            {
                var t = TryExtractInlineLiteral(inline);
                if (!string.IsNullOrEmpty(t))
                {
                    sb.Append(t).Append('\n');
                }
            }
        }
        var s = sb.ToString().TrimEnd('\n');
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string? TryExtractInlineLiteral(Inline inline)
    {
        var sb = new System.Text.StringBuilder();
        CollectLiteral(inline, sb);
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static void CollectLiteral(Inline node, System.Text.StringBuilder sb)
    {
        switch (node)
        {
            case LiteralInline lit: sb.Append(lit.Content); break;
            case HtmlEntityInline e: sb.Append(e.Transcoded.ToString()); break;
            case LineBreakInline lb: sb.Append(lb.IsHard ? '\n' : ' '); break;
            case CodeInline c: sb.Append(c.Content); break;
            case HtmlInline h: sb.Append(h.Tag); break;
            case ContainerInline c:
                foreach (var child in c)
                {
                    CollectLiteral(child, sb);
                }
                break;
        }
    }
}
