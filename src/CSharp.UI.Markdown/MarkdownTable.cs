using System.Globalization;
using CSharp.UI;
using Markdig.Extensions.Tables;

namespace CSharp.UI.Markdown;

/// <summary>
/// Markdown 表格的专用布局 View（internal，不演进为公开 Grid）。
/// </summary>
/// <remarks>
/// <para>
/// 构造参数 = 已建好的行/cell <see cref="VStack"/> 视图（Build 期一次建全，Measure 不建 view）。
/// <paramref name="availableWidth"/> 来自 <see cref="Responsive"/> 的父有效宽——
/// <see cref="Measure"/> 在 HScroll 的 <c>MaxWidth=∞</c> 约束下仍按 captured available 分配；
/// <see cref="Arrange"/> 收到超宽 bounds 不覆盖 captured。
/// </para>
/// <para>
/// 两段列宽：min-content（cell 内最长不可断单元——拉丁最长词/CJK/emoji 字素簇/code token，含容器 chrome）
/// + pref-content（自然宽）。分配：<c>Σpref ≤ W</c> → pref；<c>Σmin ≤ W &lt; Σpref</c> →
/// 按 (pref−min) 权重压缩至 <c>Σ=W</c>，clamp <c>[min,pref]</c>；<c>Σmin > W</c> → 列宽=min
/// （自然超宽由外层 <see cref="ScrollView"/> 横滚承载）。
/// </para>
/// </remarks>
internal sealed class MarkdownTable : View
{
    private readonly IReadOnlyList<IReadOnlyList<View>> _rows;
    private readonly double _availableWidth;
    private readonly double _cellPadding;
    private readonly int _columnCount;
    private IReadOnlyList<View>? _children;

    /// <param name="rows">每行 cell 视图；缺 cell 由构造按最大列补空 VStack。</param>
    /// <param name="availableWidth">父有效宽；<see cref="double.PositiveInfinity"/> 无界。</param>
    /// <param name="cellPadding">cell 单侧水平 padding。</param>
    public MarkdownTable(
        IReadOnlyList<IReadOnlyList<View>> rows,
        double availableWidth,
        double cellPadding = 8)
    {
        _rows = rows;
        _availableWidth = availableWidth;
        _cellPadding = cellPadding;
        _columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
    }

    internal override IReadOnlyList<View> Children
    {
        get
        {
            // 展平：[分隔条?, cell0, cell1, ..., 分隔条?, cell0, ...]
            return _children ??= BuildChildren();
        }
    }

    private List<View> BuildChildren()
    {
        var list = new List<View>();
        for (var r = 0; r < _rows.Count; r++)
        {
            if (r > 0)
            {
                list.Add(new Rectangle(Color.LightGray) { FixedSize = new Size(0, 1) });
            }

            foreach (var cell in _rows[r])
            {
                list.Add(cell);
            }

            // 缺 cell 补空（按最大列宽）。
            for (var i = _rows[r].Count; i < _columnCount; i++)
            {
                list.Add(new VStack());
            }
        }

        return list;
    }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        if (_columnCount == 0 || _rows.Count == 0)
        {
            node.DesiredSize = Size.Zero;
            return;
        }

        // —— 两段列宽：逐 (r,c) 求 (min, pref)；pref measure 先行把排版 commit 到 ViewCache ——
        var minWidths = new double[_columnCount];
        var prefWidths = new double[_columnCount];
        for (var r = 0; r < _rows.Count; r++)
        {
            var rowBase = r * _columnCount + r; // r 行前有 r 个分隔条
            for (var c = 0; c < _columnCount; c++)
            {
                var cellNode = node.ChildList[rowBase + c];
                // pref：自然宽下 measure（RichText 提交 layout handle 到 ViewCache）。
                context.Measure(cellNode, new LayoutConstraints(0, double.PositiveInfinity, 0, double.PositiveInfinity));
                var prefW = cellNode.DesiredSize.Width;
                if (prefW > prefWidths[c]) prefWidths[c] = prefW;

                // min：从已提交排版/真实 node 树取（无探测、无新 View/ViewNode）。
                var minW = MinContent(cellNode);
                if (minW > minWidths[c]) minWidths[c] = minW;
            }
        }

        // padding 加回（cell 内容宽 + 两侧 padding）。
        for (var c = 0; c < _columnCount; c++)
        {
            minWidths[c] += _cellPadding * 2;
            prefWidths[c] += _cellPadding * 2;
        }

        // —— 按列宽测 cell、行高 = max ——
        var widths = AllocateColumns(minWidths, prefWidths, _availableWidth);
        var rowHeights = new double[_rows.Count];
        var childIndex = 0;
        for (var r = 0; r < _rows.Count; r++)
        {
            if (r > 0)
            {
                childIndex++; // 跳过分隔条
            }

            var maxH = 0.0;
            for (var c = 0; c < _columnCount; c++)
            {
                var cellNode = node.ChildList[childIndex + c];
                var w = widths[c] - _cellPadding * 2;
                context.Measure(cellNode, new LayoutConstraints(0, Math.Max(0, w), 0, double.PositiveInfinity));
                var h = cellNode.DesiredSize.Height;
                if (h > maxH) maxH = h;
            }

            rowHeights[r] = maxH;
            childIndex += _columnCount;
        }

        var totalW = widths.Sum();
        var totalH = rowHeights.Sum() + (_rows.Count - 1) * 1.0;
        node.ViewCache = new TableLayout(widths, rowHeights, totalW, totalH);
        node.DesiredSize = new Size(totalW, totalH);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        if (node.ViewCache is not TableLayout layout)
        {
            return;
        }

        var childIndex = 0;
        var y = bounds.Y;
        for (var r = 0; r < _rows.Count; r++)
        {
            if (r > 0)
            {
                var barNode = node.ChildList[childIndex++];
                context.Arrange(barNode, new Rect(bounds.X, y, layout.TotalWidth, 1));
                y += 1;
            }

            var x = bounds.X;
            var rowH = layout.RowHeights[r];
            for (var c = 0; c < _columnCount; c++)
            {
                var cellNode = node.ChildList[childIndex + c];
                var w = layout.ColumnWidths[c];
                context.Arrange(cellNode, new Rect(x + _cellPadding, y, Math.Max(0, w - _cellPadding * 2), rowH));
                x += w;
            }
            childIndex += _columnCount;
            y += rowH;
        }
    }

    /// <summary>
    /// Responsive 换档时 captured <see cref="_availableWidth"/> 是新布局输入——
    /// reconciler 默认 equivalent=true 会跳过重 measure，新 table 持旧 <see cref="TableLayout"/>。
    /// 必须比较 captured 输入。
    /// </summary>
    internal override bool LayoutPropertiesEquivalent(View other)
        => other is MarkdownTable t
           && t._availableWidth.Equals(_availableWidth)
           && t._cellPadding.Equals(_cellPadding)
           && t._columnCount == _columnCount
           && t._rows.Count == _rows.Count;

    // —— min-content：从真实 node + 已测 layout 几何取，零新 View/ViewNode ——

    /// <summary>
    /// 单 node 的 min-content 宽：文本视图取已测 layout 的不可断 token 最大 caret 宽差；
    /// 容器视图递归组合——HStack=Σchild min + (pref−Σchild pref) 固有 chrome；
    /// VStack=max child min；单 child wrapper=child min + (parent pref−child pref) padding。
    /// </summary>
    private static double MinContent(ViewNode node)
    {
        var prefW = node.DesiredSize.Width;

        switch (node.View)
        {
            case RichText rt:
                return MinContentOfRichText(node, rt);
            case Text t:
                return MinContentOfPlainText(node, t);
            case Image:
                // 图片可缩到任意窄（等比缩语义）→ min-content = 0。
                return 0;
            case MarkdownTable nested:
                // 嵌套表自然宽度是其自身最小（列已按内部分配）。
                return nested.MinWidthOf(node);
            case VStack _:
            case ScrollView _:
            case Responsive _:
            {
                // 垂直容器：min = max child min。
                var max = 0.0;
                foreach (var child in node.Children)
                {
                    var w = MinContent(child);
                    if (w > max) max = w;
                }
                return max;
            }
            case HStack _:
            {
                // 水平容器：min = Σ child min + (pref − Σ child pref) 固有 marker/spacing。
                var sumMin = 0.0;
                var sumPref = 0.0;
                foreach (var child in node.Children)
                {
                    sumMin += MinContent(child);
                    sumPref += child.DesiredSize.Width;
                }
                var chrome = prefW - sumPref; // marker/padding/spacing 等固有宽
                return sumMin + Math.Max(0, chrome);
            }
            default:
            {
                // 通用单 child wrapper（如 padding 修饰）：min = child min + (pref − child pref)。
                if (node.Children.Count == 1)
                {
                    var child = node.Children[0];
                    var chrome = prefW - child.DesiredSize.Width;
                    return MinContent(child) + Math.Max(0, chrome);
                }

                // 多 child 未知容器 → 退到 pref（最保守；此类视图在本 adapter 不产）。
                return prefW;
            }
        }
    }

    /// <summary>嵌套表 min-content：各列 min 之和（不重复访问子 node）。</summary>
    private double MinWidthOf(ViewNode node)
    {
        // 已被外层 measure 过的列宽在 TableLayout cache 中；无则按列递归取 cell min。
        if (node.ViewCache is TableLayout cached)
        {
            return cached.ColumnWidths.Sum();
        }

        // 未被 measure（不应到达）→ 按行逐 cell 求 min 再求列和。
        var colMin = new double[_columnCount];
        for (var r = 0; r < _rows.Count; r++)
        {
            var rowBase = r * _columnCount + r;
            for (var c = 0; c < _columnCount; c++)
            {
                var w = MinContent(node.ChildList[rowBase + c]);
                if (w > colMin[c]) colMin[c] = w;
            }
        }

        return colMin.Sum() + _cellPadding * 2 * _columnCount;
    }

    /// <summary>富文本 min-content：已测 layout 按 token 的 UTF-16 区间取 caret 宽差（含字体/样式真实宽）。</summary>
    private static double MinContentOfRichText(ViewNode node, RichText rt)
    {
        var text = rt.Content.Text;
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // ViewCache 联合：rich=RichTextViewCache（含 Layout handle）；plain=TextLayoutResult。
        return node.ViewCache switch
        {
            RichText.RichTextViewCache { Layout: { } richLayout } => MaxTokenWidth(richLayout, text),
            TextLayoutResult plainLayout => MaxTokenWidth(plainLayout, text),
            _ => node.DesiredSize.Width, // 无缓存（cell 未 pref measure 过——不应到达）→ 退到 pref。
        };
    }

    /// <summary>plain 文本 min-content：TextLayoutResult caret 表。</summary>
    private static double MinContentOfPlainText(ViewNode node, Text t)
    {
        var content = t.Content;
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return node.ViewCache is TextLayoutResult layout
            ? MaxTokenWidth(layout, content)
            : node.DesiredSize.Width;
    }

    /// <summary>已测 layout 上所有不可断 token 的最大宽（caret 相邻边界差）。</summary>
    private static double MaxTokenWidth(RichTextLayoutResult layout, string text)
    {
        var max = 0.0;
        foreach (var (start, end) in UnbreakableTokens(text))
        {
            var w = TokenWidth(layout, start, end);
            if (w > max)
            {
                max = w;
            }
        }

        return max;
    }

    /// <summary>同上，plain layout。</summary>
    private static double MaxTokenWidth(TextLayoutResult layout, string text)
    {
        var max = 0.0;
        foreach (var (start, end) in UnbreakableTokens(text))
        {
            var w = TokenWidth(layout, start, end);
            if (w > max)
            {
                max = w;
            }
        }

        return max;
    }

    /// <summary>
    /// token [start, end) 在已测排版中的实际宽——按行取行内 caret 差（token 自身不跨行：
    /// 空白/硬换行是天然断点；排版器在 token 内断行时按行分段取 max）。
    /// </summary>
    private static double TokenWidth(RichTextLayoutResult layout, int start, int end)
    {
        var startLine = LineIndexOfStop(layout.Lines, start);
        var endLine = LineIndexOfStop(layout.Lines, end);
        if (startLine == endLine)
        {
            return Math.Abs(layout.GetCaretX(end) - layout.GetCaretX(start));
        }

        // 跨行：token 在每行的行内片段宽取最大（行首 token 不归并上行末 caret）。
        var max = 0.0;
        for (var li = startLine; li <= endLine; li++)
        {
            var segStart = Math.Max(start, layout.Lines[li].Start);
            var segEnd = Math.Min(end, layout.Lines[li].End);
            if (segStart < segEnd)
            {
                var w = Math.Abs(layout.GetCaretX(segEnd) - layout.GetCaretX(segStart));
                if (w > max) max = w;
            }
        }

        return max;
    }

    /// <summary>UTF-16 stop 所属行：Lines[i].End 半开——stop 等于 End 归下一行（行尾 caret 归上行）。</summary>
    private static int LineIndexOfStop(IReadOnlyList<TextLineInfo> lines, int stop)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (stop < lines[i].End || i == lines.Count - 1)
            {
                return i;
            }
        }

        return lines.Count - 1;
    }

    /// <summary>同上，plain layout。</summary>
    private static double TokenWidth(TextLayoutResult layout, int start, int end)
    {
        var startLine = LineIndexOfStop(layout.Lines, start);
        var endLine = LineIndexOfStop(layout.Lines, end);
        if (startLine == endLine)
        {
            return Math.Abs(layout.GetCaretX(end) - layout.GetCaretX(start));
        }

        var max = 0.0;
        for (var li = startLine; li <= endLine; li++)
        {
            var segStart = Math.Max(start, layout.Lines[li].Start);
            var segEnd = Math.Min(end, layout.Lines[li].End);
            if (segStart < segEnd)
            {
                var w = Math.Abs(layout.GetCaretX(segEnd) - layout.GetCaretX(segStart));
                if (w > max) max = w;
            }
        }

        return max;
    }

    /// <summary>
    /// 不可断 token 的 UTF-16 [start, end) 枚举：拉丁按空白断词（词内不换行），
    /// CJK/emoji/组合序列/代理对单字素独立——无 substring 分配，只产区间。
    /// </summary>
    private static IEnumerable<(int Start, int End)> UnbreakableTokens(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        var n = starts.Length;

        for (var i = 0; i < n; i++)
        {
            var gStart = starts[i];
            var gEnd = i + 1 < n ? starts[i + 1] : text.Length;
            if (char.IsWhiteSpace(text[gStart]))
            {
                continue;
            }

            if (IsUnbreakableUnit(text, gStart, gEnd))
            {
                yield return (gStart, gEnd);
                continue;
            }

            // 拉丁词：连续非空白非宽字符 —— 词内不换。
            var wEnd = gEnd;
            while (i + 1 < n)
            {
                var s = starts[i + 1];
                var e = i + 2 < n ? starts[i + 2] : text.Length;
                if (char.IsWhiteSpace(text[s]) || IsUnbreakableUnit(text, s, e))
                {
                    break;
                }

                wEnd = e;
                i++;
            }

            yield return (gStart, wEnd);
        }
    }

    /// <summary>是否单字素独立（CJK/emoji/组合序列/ZWJ/代理对 → 一字素一断点）。</summary>
    private static bool IsUnbreakableUnit(string text, int s, int e)
    {
        if (e - s > 1)
        {
            return true; // surrogate/组合序列/ZWJ
        }

        var c = text[s];
        return c >= 0x1100 && (
            c <= 0x115F || // Hangul Jamo
            (c >= 0x2E80 && c <= 0x9FFF) || // CJK Radicals..CJK Unified
            (c >= 0xA000 && c <= 0xA4CF) || // Yi
            (c >= 0xAC00 && c <= 0xD7A3) || // Hangul Syllables
            (c >= 0xF900 && c <= 0xFAFF) || // CJK Compat Ideographs
            (c >= 0xFE30 && c <= 0xFE4F) || // CJK Compat Forms
            (c >= 0xFF00 && c <= 0xFF60) || // Fullwidth Forms
            char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Surrogate
                                       or System.Globalization.UnicodeCategory.NonSpacingMark
                                       or System.Globalization.UnicodeCategory.SpacingCombiningMark
                                       or System.Globalization.UnicodeCategory.EnclosingMark);
    }

    // —— 列宽分配 ——

    /// <summary>Σpref ≤ W → pref；Σmin ≤ W &lt; Σpref → 按 (pref−min) 压缩；Σmin > W → min。</summary>
    private static double[] AllocateColumns(double[] min, double[] pref, double w)
    {
        var n = min.Length;
        var widths = new double[n];
        var sumPref = pref.Sum();
        var sumMin = min.Sum();

        if (double.IsInfinity(w) || sumPref <= w)
        {
            Array.Copy(pref, widths, n);
            return widths;
        }

        if (sumMin >= w)
        {
            Array.Copy(min, widths, n);
            return widths;
        }

        // 按 (pref − min) 权重压缩到 Σ=w。
        var budget = sumPref - w;
        var flexibility = 0.0;
        for (var i = 0; i < n; i++)
        {
            flexibility += pref[i] - min[i];
        }

        if (flexibility <= 0)
        {
            Array.Copy(min, widths, n);
            return widths;
        }

        for (var i = 0; i < n; i++)
        {
            var shrink = budget * ((pref[i] - min[i]) / flexibility);
            widths[i] = Math.Clamp(pref[i] - shrink, min[i], pref[i]);
        }

        return widths;
    }

    private sealed record TableLayout(double[] ColumnWidths, double[] RowHeights, double TotalWidth, double TotalHeight);
}
