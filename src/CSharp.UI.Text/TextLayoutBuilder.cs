using SkiaSharp;

namespace CSharp.UI.Skia;

/// <summary>
/// 把 <see cref="TextLayoutRequest"/> 构建为 <see cref="TextLayoutResult"/>：
/// shaping、按字素/单词断行、对齐、省略与字素级光标映射。
/// </summary>
/// <remarks>
/// 坐标约定：所有坐标相对文本块左上角，单位为逻辑像素。
/// 行范围 [Start, End) 连续覆盖可见文本（段落最后一个换行符算在上一行末尾，若被省略则不包含）。
/// <see cref="TextLayoutResult.CaretXs"/> 在每个字素边界上给出一个 X：
/// 行尾空白不推进光标（与绘制宽度一致），因此同一行内光标 X 单调不减；
/// 跨行时光标回到下一行起点，因此全局并非单调（单行文本除外）。
/// </remarks>
internal sealed class TextLayoutBuilder
{
    /// <summary>省略号字符（首版固定为单字符 …）。</summary>
    public const string Ellipsis = "…";

    private readonly SkiaFontResolver _fonts;

    public TextLayoutBuilder(SkiaFontResolver fonts) => _fonts = fonts;

    /// <summary>执行一次完整排版。</summary>
    public TextLayoutResult Build(TextLayoutRequest request)
    {
        var text = request.Text;
        var font = request.Font;
        var primary = _fonts.ResolvePrimary(font);
        var primaryInstance = _fonts.GetFontInstance(primary, font.Size);
        var metrics = primaryInstance.Font.Metrics;

        var ascent = Math.Max(0.1, -metrics.Ascent);
        var descent = Math.Max(0.1, metrics.Descent);
        var fontHeight = ascent + descent;
        var lineHeight = request.LineHeight > 0
            ? request.LineHeight
            : Math.Max(1.0, fontHeight + Math.Max(0, metrics.Leading));
        var leading = Math.Max(0, lineHeight - fontHeight);

        var graphemeStarts = Graphemes.Starts(text);
        var trimBoundaries = BuildTrimBoundaries(text, graphemeStarts);
        var xAt = new double[text.Length + 1];
        var paragraphs = SplitParagraphs(text);
        foreach (var paragraph in paragraphs)
        {
            FillParagraphX(text, paragraph, graphemeStarts, xAt, primary, font, request.LetterSpacing);
        }

        var lines = BuildLines(request, text, graphemeStarts, trimBoundaries, xAt, paragraphs);

        // MaxLines：超出部分不可见。
        var maxLines = request.MaxLines > 0 ? request.MaxLines : int.MaxValue;
        var truncatedByLines = lines.Count > maxLines;
        if (truncatedByLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
        }

        var lastLine = lines[^1];
        var ellipsisAllowed = request.Trimming == TextTrimming.Ellipsis && !double.IsPositiveInfinity(request.MaxWidth);
        var ellipsisWidth = 0.0;
        var keepEnd = lastLine.TrimEnd;
        var needsEllipsis = ellipsisAllowed && (truncatedByLines || ContentWidth(lastLine, xAt) > request.MaxWidth);
        string visibleText;
        if (needsEllipsis)
        {
            ellipsisWidth = MeasureEllipsis(font, primary, request.LetterSpacing);
            keepEnd = FindEllipsisEnd(text, graphemeStarts, trimBoundaries, xAt, lastLine, request.MaxWidth, ellipsisWidth);
            visibleText = string.Concat(text.AsSpan(0, keepEnd), Ellipsis);
            lastLine.TrimEnd = keepEnd;
            lastLine.End = visibleText.Length;
            lastLine.EllipsisWidth = ellipsisWidth;
        }
        else if (truncatedByLines)
        {
            visibleText = text[..lastLine.TrimEnd];
            lastLine.End = visibleText.Length;
        }
        else
        {
            visibleText = text;
        }

        foreach (var line in lines)
        {
            line.ContentWidth = ContentWidth(line, xAt);
        }

        var maxLineWidth = 0.0;
        foreach (var line in lines)
        {
            maxLineWidth = Math.Max(maxLineWidth, line.ContentWidth);
        }

        foreach (var line in lines)
        {
            line.OffsetX = request.Alignment switch
            {
                TextAlignment.Center => (maxLineWidth - line.ContentWidth) / 2,
                TextAlignment.Right => maxLineWidth - line.ContentWidth,
                _ => 0,
            };
        }

        var lineInfos = new List<TextLineInfo>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var top = i * lineHeight;
            var baseline = top + (leading / 2) + ascent;
            lineInfos.Add(new TextLineInfo(line.Start, line.End, new Rect(line.OffsetX, top, line.ContentWidth, lineHeight), baseline));
        }

        var caretStops = Graphemes.Starts(visibleText);
        var caretXs = new double[caretStops.Length];
        for (var i = 0; i < caretStops.Length; i++)
        {
            var stop = caretStops[i];
            if (needsEllipsis && stop >= keepEnd)
            {
                var line = lines[^1];
                caretXs[i] = line.OffsetX + (xAt[keepEnd] - xAt[line.Start]) + ((stop - keepEnd) * ellipsisWidth);
            }
            else
            {
                var line = lines[LineIndexForStop(lines, stop)];
                caretXs[i] = line.OffsetX + (xAt[Math.Min(stop, line.TrimEnd)] - xAt[line.Start]);
            }
        }

        var size = new Size(maxLineWidth, Math.Max(lineHeight, lines.Count * lineHeight));
        return new TextLayoutResult(
            visibleText,
            font,
            request.PixelScale,
            size,
            lineInfos,
            caretStops,
            caretXs,
            isTrimmed: visibleText.Length < text.Length,
            letterSpacing: request.LetterSpacing);
    }

    /// <summary>把文本切分为段落：[内容起点, 内容终点) 与含换行符的 [起点, 终点)。</summary>
    private static List<Paragraph> SplitParagraphs(string text)
    {
        var paragraphs = new List<Paragraph>();
        var index = 0;
        while (true)
        {
            var start = index;
            var contentEnd = start;
            while (contentEnd < text.Length && !Graphemes.IsLineBreak(text[contentEnd]))
            {
                contentEnd++;
            }

            var end = contentEnd;
            if (end < text.Length)
            {
                end++;
                if (text[end - 1] == '\r' && end < text.Length && text[end] == '\n')
                {
                    end++;
                }
            }

            paragraphs.Add(new Paragraph(start, contentEnd, end));
            if (end >= text.Length)
            {
                break;
            }

            index = end;
        }

        return paragraphs;
    }

    /// <summary>对单个段落做 shaping，并记录每个字素边界的 X（相对段落内容起点）。</summary>
    private void FillParagraphX(string text, Paragraph paragraph, int[] graphemeStarts, double[] xAt, SKTypeface primary, FontDescription font, double letterSpacing)
    {
        var glyphs = new List<ShapedGlyph>();
        foreach (var run in FontRunSplitter.Shape(text, paragraph.Start, paragraph.ContentEnd, graphemeStarts, _fonts, primary, font))
        {
            glyphs.AddRange(run.Glyphs);
        }

        var spacing = letterSpacing;
        var index = Graphemes.BoundaryAtOrBefore(graphemeStarts, paragraph.Start);
        var glyphIndex = 0;
        var processed = 0.0;
        var x = 0.0;
        for (; index < graphemeStarts.Length; index++)
        {
            var boundary = graphemeStarts[index];
            if (boundary > paragraph.ContentEnd)
            {
                break;
            }

            while (glyphIndex < glyphs.Count && glyphs[glyphIndex].Cluster < boundary)
            {
                x += glyphs[glyphIndex].Advance;
                glyphIndex++;
            }

            xAt[boundary] = x + (spacing * processed);
            processed++;
        }

        // 段落终止符（换行）不推进光标。
        xAt[paragraph.End] = xAt[paragraph.ContentEnd];
    }

    /// <summary>贪心断行：CJK 按字素断行、拉丁在空白/连字符处断行，超长单词硬断。</summary>
    private static List<LineRecord> BuildLines(
        TextLayoutRequest request,
        string text,
        int[] graphemeStarts,
        int[] trimBoundaries,
        double[] xAt,
        List<Paragraph> paragraphs)
    {
        var lines = new List<LineRecord>();
        var wrap = request.Wrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(request.MaxWidth);
        var maxWidth = request.MaxWidth;
        foreach (var paragraph in paragraphs)
        {
            if (!wrap || paragraph.ContentEnd <= paragraph.Start)
            {
                lines.Add(CreateLine(text, graphemeStarts, trimBoundaries, paragraph.Start, paragraph.ContentEnd, paragraph.End));
                continue;
            }

            var lineStart = paragraph.Start;
            var startIndex = Graphemes.BoundaryAtOrBefore(graphemeStarts, lineStart);
            while (lineStart < paragraph.ContentEnd)
            {
                var cursor = startIndex;
                var lastFit = lineStart;
                var lastFitIndex = startIndex;
                while (cursor + 1 < graphemeStarts.Length)
                {
                    var next = graphemeStarts[cursor + 1];
                    if (next > paragraph.ContentEnd)
                    {
                        break;
                    }

                    if (MeasuredWidth(text, graphemeStarts, trimBoundaries, xAt, lineStart, cursor + 1) > maxWidth)
                    {
                        break;
                    }

                    lastFit = next;
                    lastFitIndex = cursor + 1;
                    cursor++;
                }

                if (lastFit >= paragraph.ContentEnd)
                {
                    lines.Add(CreateLine(text, graphemeStarts, trimBoundaries, lineStart, paragraph.ContentEnd, paragraph.End));
                    break;
                }

                var breakAt = 0;
                for (var i = lastFitIndex; i > startIndex; i--)
                {
                    if (IsBreakOpportunity(text, graphemeStarts, paragraph, graphemeStarts[i]))
                    {
                        breakAt = graphemeStarts[i];
                        break;
                    }
                }

                if (breakAt <= lineStart)
                {
                    // 单词过长且没有断行机会：在能放下的最远处硬断，保证前进。
                    breakAt = lastFit > lineStart
                        ? lastFit
                        : graphemeStarts[Math.Min(graphemeStarts.Length - 1, startIndex + 1)];
                }

                lines.Add(CreateLine(text, graphemeStarts, trimBoundaries, lineStart, breakAt, breakAt));
                lineStart = breakAt;
                startIndex = Graphemes.BoundaryAtOrBefore(graphemeStarts, lineStart);
            }
        }

        return lines;
    }

    private static LineRecord CreateLine(string text, int[] graphemeStarts, int[] trimBoundaries, int start, int contentEnd, int end)
    {
        var index = Graphemes.BoundaryAtOrBefore(graphemeStarts, contentEnd);
        var trimmed = Math.Max(start, graphemeStarts[trimBoundaries[index]]);
        return new LineRecord { Start = start, End = end, TrimEnd = trimmed };
    }

    private static bool IsBreakOpportunity(string text, int[] graphemeStarts, Paragraph paragraph, int boundary)
    {
        if (boundary <= paragraph.Start || boundary > paragraph.ContentEnd)
        {
            return false;
        }

        var previous = Graphemes.CodepointAt(text, Graphemes.PreviousBoundary(graphemeStarts, boundary));
        if (Graphemes.IsWhitespace(previous) || Graphemes.IsBreakableScript(previous))
        {
            return true;
        }

        if (previous is '-' or '\u2010' or '\u2013')
        {
            return true;
        }

        return boundary < paragraph.ContentEnd && Graphemes.IsBreakableScript(Graphemes.CodepointAt(text, boundary));
    }

    /// <summary>行内容宽度（行尾空白不占宽度）。</summary>
    private static double MeasuredWidth(string text, int[] graphemeStarts, int[] trimBoundaries, double[] xAt, int lineStart, int boundaryIndex)
    {
        var boundary = graphemeStarts[boundaryIndex];
        var trimmed = Math.Max(lineStart, graphemeStarts[trimBoundaries[boundaryIndex]]);
        return xAt[trimmed] - xAt[lineStart];
    }

    private static double ContentWidth(LineRecord line, double[] xAt) => (xAt[line.TrimEnd] - xAt[line.Start]) + line.EllipsisWidth;

    private static int FindEllipsisEnd(
        string text,
        int[] graphemeStarts,
        int[] trimBoundaries,
        double[] xAt,
        LineRecord line,
        double maxWidth,
        double ellipsisWidth)
    {
        var endIndex = Graphemes.BoundaryAtOrBefore(graphemeStarts, line.TrimEnd);
        var result = line.Start;
        for (var i = Graphemes.BoundaryAtOrBefore(graphemeStarts, line.Start) + 1; i <= endIndex; i++)
        {
            if (MeasuredWidth(text, graphemeStarts, trimBoundaries, xAt, line.Start, i) + ellipsisWidth > maxWidth)
            {
                break;
            }

            result = graphemeStarts[i];
        }

        return result;
    }

    private double MeasureEllipsis(FontDescription font, SKTypeface primary, double letterSpacing)
    {
        var instance = _fonts.GetFontInstanceForCharacter(primary, font, Ellipsis[0]);
        var width = 0.0;
        foreach (var glyph in Shaping.Shape(instance, Ellipsis, 0, Ellipsis.Length))
        {
            width += glyph.Advance;
        }

        return width + Math.Max(0, letterSpacing);
    }

    private static int LineIndexForStop(List<LineRecord> lines, int stop)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (stop >= lines[i].Start && stop <= lines[i].End)
            {
                return i;
            }
        }

        return stop < lines[0].Start ? 0 : lines.Count - 1;
    }

    private static int[] BuildTrimBoundaries(string text, int[] graphemeStarts)
    {
        var trim = new int[graphemeStarts.Length];
        for (var i = 1; i < graphemeStarts.Length; i++)
        {
            var previous = Graphemes.CodepointAt(text, graphemeStarts[i - 1]);
            trim[i] = Graphemes.IsWhitespace(previous) ? trim[i - 1] : i;
        }

        return trim;
    }

    /// <summary>段落：内容范围 [Start, ContentEnd) 与含换行符的范围 [Start, End)。</summary>
    private readonly record struct Paragraph(int Start, int ContentEnd, int End);

    /// <summary>排版过程中的一行。</summary>
    private sealed class LineRecord
    {
        public int Start { get; init; }

        public int End { get; set; }

        /// <summary>用于计算内容宽度的边界（去掉行尾空白与换行符；省略时含省略号）。</summary>
        public int TrimEnd { get; set; }

        /// <summary>省略号宽度（未省略时为 0）。</summary>
        public double EllipsisWidth { get; set; }

        public double ContentWidth { get; set; }

        public double OffsetX { get; set; }
    }
}
