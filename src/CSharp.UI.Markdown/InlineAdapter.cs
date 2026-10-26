using System.Globalization;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CSharp.UI;

namespace CSharp.UI.Markdown;

/// <summary>
/// 把 Markdig inline 树归一为 <see cref="StyledText"/>。
/// </summary>
/// <remarks>
/// <para>
/// 第一遍递归 walk：携带已解析样式栈（内层覆盖外层同字段、不同字段叠加）向下传递——深度语义在
/// 递归时即时 resolve，不靠 mark 排序猜栈深。每个产展示文本的 leaf 发一条 <see cref="StyleMark"/>
/// （按写入序严格升序、互不重叠）和可选 <see cref="InteractionMark"/>（同链接同 Id）。
/// </para>
/// <para>
/// 第二遍逐字素单遍归一（共享一份字素边界表）：游标随字素推进扫描 mark 列表（O(字素+mark)），
/// 每字素取首个覆盖它的 mark 的样式（marks 已按写入先后排 depth 序——同 offset 内层后写，取最深
/// 需显式比较 depth）。单字素被切到两个 mark（病态 AST）→ 取 depth 最大者 + 诊断计数；
/// 不外扩、不重叠、不抛。
/// </para>
/// </remarks>
internal static class InlineAdapter
{
    /// <summary>病态字素分裂诊断计数（单字素跨两个不同样式 mark 时递增，取最深栈兜底）。</summary>
    internal static int SplitGraphemeDiagnosticCount { get; private set; }

    /// <summary>把 inline 子树转成 <see cref="StyledText"/>。</summary>
    /// <param name="root">inline 根（ParagraphBlock.Inline 等）。</param>
    /// <param name="htmlPolicy">HTML 节点策略（Literal→原文；Strip→不产文本）。</param>
    /// <param name="linkPolicy">链接激活策略；null → 默认。</param>
    /// <param name="style">样式选项；null → 默认。</param>
    public static StyledText Adapt(
        Inline? root,
        HtmlPolicy htmlPolicy,
        MarkdownLinkPolicy? linkPolicy = null,
        MarkdownStyleOptions? style = null)
    {
        linkPolicy ??= MarkdownLinkPolicy.Default;
        style ??= new MarkdownStyleOptions();

        if (root is null)
        {
            return StyledText.Create(string.Empty);
        }

        var ctx = new WalkContext(
            text: new System.Text.StringBuilder(),
            styleMarks: [],
            interactionMarks: [],
            htmlPolicy: htmlPolicy,
            linkPolicy: linkPolicy,
            style: style);

        if (root is ContainerInline container)
        {
            foreach (var child in container)
            {
                WalkInline(child, ref ctx, ResolvedStyle.Empty, linkId: -1, linkPayload: null, depth: 0);
            }
        }
        else
        {
            WalkInline(root, ref ctx, ResolvedStyle.Empty, linkId: -1, linkPayload: null, depth: 0);
        }

        var displayText = ctx.Text.ToString();
        if (displayText.Length == 0)
        {
            return StyledText.Create(string.Empty);
        }

        var boundaries = GraphemeBoundaries(displayText);
        var styleRanges = NormalizeStyleRanges(displayText, ctx.StyleMarks, boundaries);
        var interactionRanges = NormalizeInteractionRanges(ctx.InteractionMarks, boundaries);
        return new StyledText(displayText, styleRanges, interactionRanges);
    }

    // —— 样式栈值对象：字段 null = 未设置（继承父层）——

    private sealed record ResolvedStyle
    {
        public static readonly ResolvedStyle Empty = new();

        public FontFamily? Family { get; init; }
        public FontWeight? Weight { get; init; }
        public bool? Italic { get; init; }
        public bool? Underline { get; init; }
        public bool? Strikethrough { get; init; }
        public Color? Foreground { get; init; }
        public Color? Background { get; init; }
        public bool? ForegroundIsAccent { get; init; }
        public double? CornerRadius { get; init; }

        /// <summary>overlay 已设置字段覆盖 base；未设置字段继承。</summary>
        public ResolvedStyle Overlay(ResolvedStyle overlay) => new()
        {
            Family = overlay.Family ?? Family,
            Weight = overlay.Weight ?? Weight,
            Italic = overlay.Italic ?? Italic,
            Underline = overlay.Underline ?? Underline,
            Strikethrough = overlay.Strikethrough ?? Strikethrough,
            Foreground = overlay.Foreground ?? Foreground,
            Background = overlay.Background ?? Background,
            ForegroundIsAccent = overlay.ForegroundIsAccent ?? ForegroundIsAccent,
            CornerRadius = overlay.CornerRadius ?? CornerRadius,
        };

        public TextStyle ToTextStyle() => new()
        {
            Family = Family,
            Weight = Weight,
            Italic = Italic,
            Underline = Underline,
            Strikethrough = Strikethrough,
            Foreground = Foreground,
            Background = Background,
            ForegroundIsAccent = ForegroundIsAccent,
            CornerRadius = CornerRadius,
        };
    }

    // mark 按写入序（DFS pre-order）严格升序、互不重叠——同一起始 offset 的内层 mark 后写
    // （外层容器在子 leaf 之前就排好样式），因此 depth 显式携带。
    private readonly record struct StyleMark(int Start, int End, ResolvedStyle Style, int Depth);
    private readonly record struct InteractionMark(int Start, int End, int Id, MarkdownLinkPayload Payload, int Depth);

    private struct WalkContext(
        System.Text.StringBuilder text,
        List<StyleMark> styleMarks,
        List<InteractionMark> interactionMarks,
        HtmlPolicy htmlPolicy,
        MarkdownLinkPolicy linkPolicy,
        MarkdownStyleOptions style)
    {
        public System.Text.StringBuilder Text = text;
        public List<StyleMark> StyleMarks = styleMarks;
        public List<InteractionMark> InteractionMarks = interactionMarks;
        public HtmlPolicy HtmlPolicy = htmlPolicy;
        public MarkdownLinkPolicy LinkPolicy = linkPolicy;
        public MarkdownStyleOptions Style = style;
        public int LinkIdCounter;
    }

    // —— 第一遍：递归 walk ——

    /// <param name="current">已解析样式栈（含本节点祖先全部叠加结果）。</param>
    /// <param name="linkId">当前 LinkInline 分配的 Id；-1 表示不在链接内。</param>
    /// <param name="depth">inline 嵌套深度（link/emphasis/code/image 等每次 +1）。</param>
    private static void WalkInline(
        Inline node,
        ref WalkContext ctx,
        ResolvedStyle current,
        int linkId,
        MarkdownLinkPayload? linkPayload,
        int depth)
    {
        switch (node)
        {
            case LiteralInline literal:
                EmitText(ref ctx, literal.Content.ToString(), current, linkId, linkPayload, depth);
                break;

            case HtmlEntityInline entity:
                EmitText(ref ctx, entity.Transcoded.ToString(), current, linkId, linkPayload, depth);
                break;

            case LineBreakInline lineBreak:
            {
                var s = ctx.Text.Length;
                ctx.Text.Append(lineBreak.IsHard ? '\n' : ' ');
                // 空白不进样式 mark；仍计入 interaction 区间。
                if (linkId >= 0 && s < ctx.Text.Length)
                {
                    ctx.InteractionMarks.Add(new InteractionMark(s, ctx.Text.Length, linkId, linkPayload!, depth));
                }
                break;
            }

            case CodeInline code:
            {
                var resolved = current.Overlay(new ResolvedStyle
                {
                    Family = ctx.Style.CodeFontFamily ?? MarkdownStyleOptions.DefaultCodeFontFamily,
                    Background = ctx.Style.CodeBackground,
                    Foreground = ctx.Style.InlineCodeForeground,
                    ForegroundIsAccent = ctx.Style.InlineCodeForeground is null,
                    CornerRadius = ctx.Style.InlineCodeCornerRadius,
                });
                EmitText(ref ctx, code.Content, resolved, linkId, linkPayload, depth + 1);
                break;
            }

            case EmphasisInline emphasis:
            {
                var resolved = current.Overlay(EmphasisOverlay(emphasis));
                foreach (var child in emphasis)
                {
                    WalkInline(child, ref ctx, resolved, linkId, linkPayload, depth + 1);
                }
                break;
            }

            case LinkInline link when !link.IsImage:
            {
                var url = link.Url ?? string.Empty;
                var allowed = ctx.LinkPolicy.IsActivationAllowed(url);
                var thisLinkId = allowed ? ++ctx.LinkIdCounter : -1;
                var payload = allowed ? new MarkdownLinkPayload(url, link.Title) : null;

                var resolved = current.Overlay(new ResolvedStyle
                {
                    Foreground = ctx.Style.LinkForeground,
                    Underline = true,
                });
                foreach (var child in link)
                {
                    WalkInline(child, ref ctx, resolved, thisLinkId, payload, depth + 1);
                }
                break;
            }

            case LinkInline imageLink when imageLink.IsImage:
            {
                var altSb = new System.Text.StringBuilder();
                foreach (var child in imageLink)
                {
                    CollectLiteralForAlt(child, altSb);
                }
                var alt = altSb.Length > 0 ? altSb.ToString() : (imageLink.Url ?? string.Empty);
                EmitText(ref ctx, "[" + alt + "]", current, linkId, linkPayload, depth);
                break;
            }

            case AutolinkInline autolink:
            {
                var url = autolink.Url;
                var allowed = ctx.LinkPolicy.IsActivationAllowed(url);
                var thisLinkId = allowed ? ++ctx.LinkIdCounter : -1;
                var payload = allowed ? new MarkdownLinkPayload(url, null) : null;
                var resolved = current.Overlay(new ResolvedStyle
                {
                    Foreground = ctx.Style.LinkForeground,
                    Underline = true,
                });
                EmitText(ref ctx, url, resolved, thisLinkId, payload, depth + 1);
                break;
            }

            case HtmlInline html:
            {
                if (ctx.HtmlPolicy == HtmlPolicy.Literal)
                {
                    EmitText(ref ctx, html.Tag, current, linkId, linkPayload, depth);
                }
                break;
            }

            case Markdig.Extensions.TaskLists.TaskList:
                // TaskList 由 BlockAdapter 在 list marker 合成（"[ ]"/"[x]"），inline 不产文本。
                break;

            case ContainerInline container:
                foreach (var child in container)
                {
                    WalkInline(child, ref ctx, current, linkId, linkPayload, depth);
                }
                break;

            default:
            {
                var literal = TryExtractLiteralText(node) ?? $"[{node.GetType().Name}]";
                var resolved = current.Overlay(new ResolvedStyle
                {
                    Foreground = ctx.Style.UnsupportedNodeStyle?.Foreground,
                    Background = ctx.Style.UnsupportedNodeStyle?.Background,
                    Underline = ctx.Style.UnsupportedNodeStyle?.Underline,
                    Strikethrough = ctx.Style.UnsupportedNodeStyle?.Strikethrough,
                });
                EmitText(ref ctx, literal, resolved, linkId, linkPayload, depth + 1);
                break;
            }
        }
    }

    /// <summary>追加展示文本并记录 mark（文本非空时）。</summary>
    private static void EmitText(
        ref WalkContext ctx,
        string content,
        ResolvedStyle style,
        int linkId,
        MarkdownLinkPayload? linkPayload,
        int depth)
    {
        if (content.Length == 0)
        {
            return;
        }

        var s = ctx.Text.Length;
        ctx.Text.Append(content);
        var e = ctx.Text.Length;

        if (!ReferenceEquals(style, ResolvedStyle.Empty))
        {
            ctx.StyleMarks.Add(new StyleMark(s, e, style, depth));
        }
        if (linkId >= 0)
        {
            ctx.InteractionMarks.Add(new InteractionMark(s, e, linkId, linkPayload!, depth));
        }
    }

    private static ResolvedStyle EmphasisOverlay(EmphasisInline emphasis)
    {
        var count = emphasis.DelimiterCount;
        var ch = emphasis.DelimiterChar;

        if (ch is '~')
        {
            return count >= 2 ? new ResolvedStyle { Strikethrough = true } : ResolvedStyle.Empty;
        }

        // '*'/'_'：奇数 → italic；≥2 → bold。
        var overlay = new ResolvedStyle();
        if (count % 2 == 1)
        {
            overlay = overlay with { Italic = true };
        }
        if (count >= 2)
        {
            overlay = overlay with { Weight = FontWeight.Bold };
        }
        return overlay;
    }

    private static string? TryExtractLiteralText(Inline node)
    {
        var sb = new System.Text.StringBuilder();
        CollectLiteralForAlt(node, sb);
        var s = sb.ToString();
        return s.Length == 0 ? null : s;
    }

    /// <summary>literal 子树解码文本（未知节点与行内图 alt 用）。</summary>
    private static void CollectLiteralForAlt(Inline node, System.Text.StringBuilder sb)
    {
        switch (node)
        {
            case LiteralInline lit: sb.Append(lit.Content); break;
            case HtmlEntityInline e: sb.Append(e.Transcoded.ToString()); break;
            case CodeInline c: sb.Append(c.Content); break;
            case LineBreakInline lb: sb.Append(lb.IsHard ? '\n' : ' '); break;
            case ContainerInline c:
                foreach (var child in c)
                {
                    CollectLiteralForAlt(child, sb);
                }
                break;
        }
    }

    // —— 第二遍：逐字素单遍归一（共用一份边界表；marks 已按写入序=升序） ——

    private static IReadOnlyList<TextStyleRange> NormalizeStyleRanges(
        string text, List<StyleMark> marks, List<int> boundaries)
    {
        var result = new List<TextStyleRange>();
        if (marks.Count == 0)
        {
            return result;
        }

        var markIndex = 0;
        ResolvedStyle? lastResolved = null;

        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var gStart = boundaries[i];
            var gEnd = boundaries[i + 1];

            while (markIndex < marks.Count && marks[markIndex].End <= gStart)
            {
                markIndex++;
            }
            if (markIndex >= marks.Count)
            {
                break;
            }

            // 收集所有与本字素有交集的 mark；取 depth 最大者。
            var bestIndex = -1;
            var bestDepth = -1;
            var probe = markIndex;
            while (probe < marks.Count && marks[probe].Start < gEnd)
            {
                var m = marks[probe];
                if (m.End > gStart)
                {
                    if (m.Start > gStart || m.End < gEnd)
                    {
                        SplitGraphemeDiagnosticCount++; // 病态：mark 只覆盖字素一部分。
                    }
                    if (m.Depth > bestDepth)
                    {
                        bestDepth = m.Depth;
                        bestIndex = probe;
                    }
                }
                probe++;
            }

            if (bestIndex < 0)
            {
                continue;
            }

            var resolved = marks[bestIndex].Style;
            // resolved 是 record → 值相等比较不分配；只有真正 transition 时才 ToTextStyle。
            if (lastResolved is { } prev && result.Count > 0 && result[^1].End == gStart
                && (ReferenceEquals(prev, resolved) || prev.Equals(resolved)))
            {
                result[^1] = result[^1] with { End = gEnd };
                continue;
            }

            var ts = resolved.ToTextStyle();
            result.Add(new TextStyleRange(gStart, gEnd, ts));
            lastResolved = resolved;
        }

        return result;
    }

    private static IReadOnlyList<TextInteractionRange> NormalizeInteractionRanges(
        List<InteractionMark> marks, List<int> boundaries)
    {
        var result = new List<TextInteractionRange>();
        if (marks.Count == 0)
        {
            return result;
        }

        var markIndex = 0;
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var gStart = boundaries[i];
            var gEnd = boundaries[i + 1];

            while (markIndex < marks.Count && marks[markIndex].End <= gStart)
            {
                markIndex++;
            }
            if (markIndex >= marks.Count)
            {
                break;
            }

            // 本字素的 interaction 归属：所有与本字素有交集的 mark 中 depth 最大者
            // （同 depth 取首个——同链接同 Id 自然合并）。
            var bestMark = -1;
            var bestDepth = -1;
            var probe = markIndex;
            while (probe < marks.Count && marks[probe].Start < gEnd)
            {
                var m = marks[probe];
                if (m.End > gStart && m.Depth > bestDepth)
                {
                    bestDepth = m.Depth;
                    bestMark = probe;
                }
                probe++;
            }

            if (bestMark < 0)
            {
                continue;
            }

            var mark = marks[bestMark];
            // 端点必须吸附到字素边界——[gStart,gEnd)，不是 [max(start,gStart),min(end,gEnd))。
            var s = gStart;
            var e = gEnd;

            if (result.Count > 0 && result[^1].Id == mark.Id && result[^1].End == s)
            {
                result[^1] = result[^1] with { End = e };
            }
            else
            {
                result.Add(new TextInteractionRange(s, e, mark.Id, mark.Payload));
            }
        }

        return result;
    }

    private static List<int> GraphemeBoundaries(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        var list = new List<int>(starts.Length + 1);
        list.AddRange(starts);
        list.Add(text.Length);
        return list;
    }
}
