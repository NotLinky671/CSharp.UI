using SkiaSharp;

namespace CSharp.UI.Skia;

/// <summary>
/// 把 <see cref="RichTextLayoutRequest"/> 构建为 <see cref="RichLayoutCore"/>：
/// 模型端点 + resolved 字体签名 + 回退 face 决定分段；每个（模型段 × face）子段独立 shaping
/// （禁止跨端点 ligature/kerning），按字素/单词断行，异字号行高/baseline 归一。
/// </summary>
/// <remarks>
/// <para>
/// 分段只由①模型端点拓扑 ②resolved Family/Size/Weight/Italic 变化 ③回退 face 变化 ④script
/// 决定——颜色/payload 值不参与。paint-only 端点处对同 face 子段**分次 shape**（每子段独立
/// HarfBuzz buffer），保证一个 glyph 绝不吞两个样式区间。
/// </para>
/// <para>
/// 行几何：行高 = max(显式 LineHeight, 行内 max(ascent)+max(descent))——显式值小于字高时按
/// 字高钳制，glyph 不越行盒；baseline = leading/2 + max(ascent)；各 run 按行基线对齐。
/// </para>
/// <para>
/// 排版结果把 shaped glyph（id + 相对段落起点的绝对偏移 + cluster 表）与每子段一个
/// <see cref="FontInstanceLease"/> 存进 <see cref="RichTextBackend"/>；栅格化按 glyph 数组直绘，
/// 不重新 shape。
/// </para>
/// </remarks>
internal sealed class RichTextLayoutBuilder
{
    private readonly SkiaFontResolver _fonts;

    public RichTextLayoutBuilder(SkiaFontResolver fonts) => _fonts = fonts;

    /// <summary>执行一次完整排版；返回持有后端资源（face 租约 + glyph 表）的共享 core。</summary>
    /// <remarks>任何一步失败：已创建的租约立即归还，不泄漏。</remarks>
    public RichLayoutCore Build(RichTextLayoutRequest request)
    {
        var styled = request.Text;
        var text = styled.Text;
        var graphemeStarts = Graphemes.Starts(text);
        var trimBoundaries = BuildTrimBoundaries(text, graphemeStarts);
        var paragraphs = SplitParagraphs(text);
        // 行内 padding（盒几何，非纯绘制扩边）：shape 阶段 pad-free 得原始 xAt/glyph points；
        // Assemble 按行内每 style-range 片段（line fragment）把左 pad 落在片段首个
        // 字形前、右 pad 落在片段末字形后——生成 padShift[boundary] 再统一偏移
        // xAt/glyph points/line infos/segments/carets。wrap 按盒宽预算
        // （padAt 事件 + 行首续片段左 pad），行几何经 padShift 后同样按盒宽重算。
        var pads = BuildInlinePadding(styled, text.Length);

        // —— 第一阶段：模型段 × 段落区间取交 → resolved 字体 → face 子段分次 shape，
        //    写 xAt（字形边界累计 X，pad-free）。
        var fontRuns = new List<FontRun>();
        var glyphRuns = new List<GlyphRun>();
        var fonts = new List<ResolvedFontIdentity>();
        var fontIds = new Dictionary<FaceKey, int>();
        var xAt = new double[text.Length + 1];
        try
        {
            ResolveAndShape(request, styled, text, paragraphs, graphemeStarts, xAt, fontRuns, glyphRuns, fonts, fontIds);
        }
        catch
        {
            foreach (var run in glyphRuns)
            {
                run.Lease.Dispose();
            }

            throw;
        }

        var backend = new RichTextBackend(glyphRuns, xAt);
        try
        {
            var core = Assemble(request, styled, text, graphemeStarts, trimBoundaries, paragraphs, xAt, pads, fontRuns, fonts, backend);
            backend = null!; // 所有权移交 core
            return core;
        }
        finally
        {
            backend?.Dispose();
        }
    }

    /// <summary>按样式区间构造 padding 几何表；无 padding 区间返回 null（零分配）。</summary>
    /// <returns>
    /// <para><c>InsidePad[b]</c>：覆盖边界 b 的 padding 区间 pad 值（range.Start ≤ b &lt; range.End）
    /// —— 折行时行首落在区间内的 fragment 需要补左 pad，用它做 wrap 预算。</para>
    /// <para><c>CumPadAt[b]</c>：Σ padAt[0..b]（range Start/End 处各 +pad 事件的前缀和）——
    /// 行内 (lineStart, b] 的 pad 事件 = CumPadAt[b] − CumPadAt[lineStart]。</para>
    /// </returns>
    private static (double[] InsidePad, double[] CumPadAt)? BuildInlinePadding(StyledText styled, int textLength)
    {
        double[]? insidePad = null;
        double[]? cumPadAt = null;
        foreach (var range in styled.Ranges)
        {
            var pad = range.Style?.InlineCodePaddingHorizontal ?? 0;
            if (pad <= 0)
            {
                continue;
            }

            insidePad ??= new double[textLength + 1];
            cumPadAt ??= new double[textLength + 1];
            // 差分写入：InsidePad 先存覆盖增量差分，CumPadAt 先存 Start/End +pad
            // 事件；扫尾一趟原地转前缀和——只 2 个结果数组，不再要 scratch。
            insidePad[range.Start] += pad;
            insidePad[range.End] -= pad;
            cumPadAt[range.Start] += pad;
            cumPadAt[range.End] += pad;
        }

        if (insidePad is null)
        {
            return null;
        }

        var cover = 0.0;
        var cum = 0.0;
        for (var i = 0; i <= textLength; i++)
        {
            cover += insidePad[i];
            cum += cumPadAt![i];
            insidePad[i] = cover;
            cumPadAt[i] = cum;
        }

        return (insidePad, cumPadAt!);
    }
    /// <summary>模型段 → resolved 字体 → 分次 shape（face 子段），写 xAt/beforeXAt
    /// 并累积租约/glyph/字体表。beforeXAt 为盒坐标（含 padding 增量），xAt 为字形坐标。</summary>
    private void ResolveAndShape(
        RichTextLayoutRequest request,
        StyledText styled,
        string text,
        List<Paragraph> paragraphs,
        int[] graphemeStarts,
        double[] xAt,
        List<FontRun> fontRuns,
        List<GlyphRun> glyphRuns,
        List<ResolvedFontIdentity> fonts,
        Dictionary<FaceKey, int> fontIds)
    {
        var paragraph = request.ParagraphFont;
        var spacing = request.LetterSpacing;
        var segments = styled.Segments;
        var segIndex = 0;

        foreach (var paragraphSpan in paragraphs)
        {
            var paragraphX = 0.0;
            var clusterOrdinal = 0;
            if (paragraphSpan.ContentEnd > paragraphSpan.Start)
            {
                // 段落内的模型段（模型段不跨端点；跳过完全在段落外的段）。
                while (segIndex < segments.Count && segments[segIndex].End <= paragraphSpan.Start)
                {
                    segIndex++;
                }

                for (var i = segIndex; i < segments.Count && segments[i].Start < paragraphSpan.ContentEnd; i++)
                {
                    var segment = segments[i];
                    var segStart = Math.Max(segment.Start, paragraphSpan.Start);
                    var segEnd = Math.Min(segment.End, paragraphSpan.ContentEnd);
                    if (segStart >= segEnd)
                    {
                        continue;
                    }

                    var font = ResolveFont(paragraph, segment.FontSignature);
                    var primary = _fonts.ResolvePrimary(font);

                    ShapeSegment(
                        text, segStart, segEnd, font, primary, graphemeStarts,
                        spacing, ref paragraphX, ref clusterOrdinal, xAt, fontRuns, glyphRuns, fonts, fontIds);
                }
            }

            xAt[paragraphSpan.End] = xAt[paragraphSpan.ContentEnd];
        }
    }

    /// <summary>
    /// 一个模型段（[segStart,segEnd)，不跨任何端点）内按 face + script 切子段并**各自 shaping**：
    /// 同 face 且同 dominant script 的簇聚合为一个 shape 子段；face 切换（回退）或 script 切换
    /// （如 Arabic→Latin、CJK→Latin）在字素边界处 Flush。每个 shape 子段 script 一致，保证
    /// <see cref="HarfBuzzSharp.Buffer.GuessSegmentProperties"/> 的推断与该段真实 script 一致。
    /// </summary>
    private void ShapeSegment(
        string text,
        int segStart,
        int segEnd,
        FontDescription font,
        SKTypeface primary,
        int[] graphemeStarts,
        double spacing,
        ref double paragraphX,
        ref int clusterOrdinal,
        double[] xAt,
        List<FontRun> fontRuns,
        List<GlyphRun> glyphRuns,
        List<ResolvedFontIdentity> fonts,
        Dictionary<FaceKey, int> fontIds)
    {
        FontInstance? current = null;
        var currentScript = HarfBuzzSharp.Script.Unknown;
        var runStart = segStart;
        var runEnd = segStart;
        var runBaseOrdinal = 0;
        var runBaseX = 0.0;
        var ordinal = clusterOrdinal;
        var x = paragraphX;

        void Flush()
        {
            if (current is null || runEnd <= runStart)
            {
                return;
            }

            // 一次 shaping 同时产出 glyph 表与行内 X：xAt 按“Cluster < 边界”的 advance 累加
            //（与 plain FillParagraphX 同一语义——连字/组合簇的 advance 归到簇起点边界，
            // 簇内中间 caret 不单独分摊；字距每字素只加一次）。
            var glyphs = Shaping.Shape(current, text, runStart, runEnd - runStart);
            var ids = new ushort[glyphs.Count];
            var points = new SKPoint[glyphs.Count];
            var clusters = new int[glyphs.Count];
            var ord = runBaseOrdinal;
            var runX = runBaseX;
            var glyphIndex = 0;
            var cursor = Graphemes.BoundaryAtOrBefore(graphemeStarts, runStart);
            for (; cursor + 1 < graphemeStarts.Length && graphemeStarts[cursor] < runEnd; cursor++)
            {
                var boundary = graphemeStarts[cursor];
                // 累加 boundary 之前的 glyph advance（cluster < boundary）。
                while (glyphIndex < glyphs.Count && glyphs[glyphIndex].Cluster < boundary)
                {
                    runX += glyphs[glyphIndex].Advance;
                    glyphIndex++;
                }
                if (boundary >= runStart)
                {
                    xAt[boundary] = runX + (spacing * ord);
                    ord++;
                }
            }

            // run 末尾（runEnd）也写一个边界：所有 glyph advance 已计入。
            while (glyphIndex < glyphs.Count)
            {
                runX += glyphs[glyphIndex].Advance;
                glyphIndex++;
            }
            if (runEnd <= text.Length)
            {
                xAt[runEnd] = runX + (spacing * ord);
            }

            // glyph 画布偏移：段落绝对 X（相对段落内容起点，含字距换算），cluster 记录绝对 UTF-16。
            var drawX = runBaseX;
            var drawOrd = runBaseOrdinal;
            var drawCursor = Graphemes.BoundaryAtOrBefore(graphemeStarts, runStart);
            for (var i = 0; i < glyphs.Count; i++)
            {
                while (drawCursor + 1 < graphemeStarts.Length && graphemeStarts[drawCursor + 1] <= glyphs[i].Cluster)
                {
                    drawCursor++;
                    drawOrd++;
                }

                ids[i] = glyphs[i].Glyph;
                clusters[i] = glyphs[i].Cluster;
                // Points.x = drawX + spacing*drawOrd + offsetX —— 其中 drawX 只含 advance（不含字距），
                // spacing*drawOrd 是该 glyph 起点字素序号对应的字距累计；与 xAt 的 spacing*ord 同源。
                points[i] = new SKPoint((float)(drawX + (spacing * drawOrd) + glyphs[i].OffsetX), (float)glyphs[i].OffsetY);
                drawX += glyphs[i].Advance;
            }

            var lease = _fonts.AcquireLease(current);
            glyphRuns.Add(new GlyphRun(runStart, runEnd, lease, current, ids, points, clusters));
            fontRuns.Add(new FontRun(runStart, runEnd, FontIdFor(fonts, fontIds, font, current), current));
            x = runX;
            ordinal = ord;
        }

        var index = Graphemes.BoundaryAtOrBefore(graphemeStarts, segStart);
        for (; index + 1 < graphemeStarts.Length; index++)
        {
            var clusterStart = graphemeStarts[index];
            if (clusterStart >= segEnd)
            {
                break;
            }

            var clusterEnd = Math.Min(graphemeStarts[index + 1], segEnd);
            var codepoint = Graphemes.CodepointAt(text, clusterStart);
            var instance = _fonts.GetFontInstanceForCharacter(primary, font, codepoint);
            // 每簇 dominant script：Common/Inherited/Unknown 继承当前 run script（标点/组合不切开 run）。
            var script = UnicodeScriptOf(codepoint);
            if (script.Equals(HarfBuzzSharp.Script.Common) || script.Equals(HarfBuzzSharp.Script.Inherited) || script.Equals(HarfBuzzSharp.Script.Unknown))
            {
                script = currentScript;
            }

            if (current is null)
            {
                runStart = clusterStart;
                runBaseOrdinal = ordinal;
                runBaseX = x;
            }
            else if (!ReferenceEquals(current, instance) || script != currentScript)
            {
                // face 或 script 边界（Arabic↔Latin 等）：先 flush 已聚合 run，保证每个
                // shape 子段 script/方向一致（GuessSegmentProperties 推断与真实 script 相同）。
                Flush();
                runStart = clusterStart;
                runBaseOrdinal = ordinal;
                runBaseX = x;
            }

            current = instance;
            currentScript = script;
            runEnd = clusterEnd;
        }

        Flush();
        paragraphX = x;
        clusterOrdinal = ordinal;
    }

    /// <summary>第二阶段：断行 → 行 metrics → 对齐 → canonical segment + caret 表 → core。</summary>
    private RichLayoutCore Assemble(
        RichTextLayoutRequest request,
        StyledText styled,
        string text,
        int[] graphemeStarts,
        int[] trimBoundaries,
        List<Paragraph> paragraphs,
        double[] xAt,
        (double[] InsidePad, double[] CumPadAt)? pads,
        List<FontRun> fontRuns,
        List<ResolvedFontIdentity> fonts,
        RichTextBackend backend)
    {
        var lines = BuildLines(request, text, graphemeStarts, trimBoundaries, xAt, paragraphs, pads?.InsidePad, pads?.CumPadAt);
        var metrics = ComputeLineMetrics(lines, fontRuns, request.Text, request);

        // 行内盒 padding：每个 style-range 在每个折行行内是一个连续片段，
        // 片段首字形前 +hPad、片段末字形后 +hPad——生成 padShift[boundary]
        // （盒坐标累计偏移）。wrap 已按盒宽预算（含 pad），此处把片段级
        // padding 并入 xAt（累计 shift）与 glyph points。
        var padShift = pads is null ? null : ComputeLineFragmentPadShift(styled, lines);
        if (padShift is not null)
        {
            ApplyPadShift(padShift, xAt, backend.GlyphRuns, lines, pads!.Value.InsidePad);
        }

        var lineInfos = new List<TextLineInfo>(lines.Count);
        var top = 0.0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var contentWidth = xAt[line.TrimEnd] - xAt[line.Start]
                + (pads?.InsidePad[line.Start] ?? 0) - (pads?.InsidePad[line.TrimEnd] ?? 0);
            lineInfos.Add(new TextLineInfo(line.Start, line.End, new Rect(0, top, contentWidth, metrics[i].Height), top + metrics[i].Baseline));
            top += metrics[i].Height;
        }

        var maxLineWidth = 0.0;
        foreach (var line in lineInfos)
        {
            maxLineWidth = Math.Max(maxLineWidth, line.Bounds.Width);
        }

        for (var i = 0; i < lineInfos.Count; i++)
        {
            var line = lineInfos[i];
            var offsetX = request.Alignment switch
            {
                TextAlignment.Center => (maxLineWidth - line.Bounds.Width) / 2,
                TextAlignment.Right => maxLineWidth - line.Bounds.Width,
                _ => 0,
            };
            if (offsetX != 0)
            {
                lineInfos[i] = line with { Bounds = new Rect(offsetX, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height) };
            }
        }

        var segments = BuildCanonicalSegments(lines, lineInfos, fontRuns, xAt, pads?.InsidePad, styled);

        var caretXs = new double[graphemeStarts.Length];
        for (var i = 0; i < graphemeStarts.Length; i++)
        {
            var stop = graphemeStarts[i];
            var lineIndex = LineIndexForStop(lines, stop);
            var line = lines[lineIndex];
            var stopX = Math.Min(stop, line.TrimEnd);
            // caret = 行盒原点起算位置：下一片段左 pad 不属本片段盒右缘。
            caretXs[i] = lineInfos[lineIndex].Bounds.X + (xAt[stopX] - xAt[line.Start])
                + (pads?.InsidePad[line.Start] ?? 0)
                - NextFragmentLeftPad(styled, line, stopX, pads?.InsidePad);
        }

        var caretsByLine = BuildLineCarets(lines, lineInfos, graphemeStarts, caretXs, text);

        var size = new Size(maxLineWidth, lineInfos.Count == 0 ? 0 : top);
        var rasterBounds = ComputeRasterBounds(backend.GlyphRuns, lines, lineInfos, xAt, size);
        return new RichLayoutCore(
            text,
            request.ParagraphFont,
            request.PixelScale,
            request.LetterSpacing,
            size,
            lineInfos,
            segments,
            fonts,
            graphemeStarts,
            caretXs,
            caretsByLine,
            rasterBounds,
            backend);
    }

    /// <summary>
    /// 按行计算 style-range 片段级 padShift：每个带 hPad 的样式区间与每行取交，
    /// 交区间的首个 boundary +pad、末尾 boundary +pad（片段级一次，非每字素）。
    /// padShift[b] = 边界 b 处盒坐标相对字形坐标的偏移；ApplyPadShift 把它并入
    /// xAt 与 glyph points 后，xAt[b] = 原始字形 X + padShift[b]。
    /// </summary>
    private static double[]? ComputeLineFragmentPadShift(StyledText styled, List<LineRecord> lines)
    {
        double[]? shift = null;
        var ranges = styled.Ranges;
        if (ranges.Count == 0)
        {
            return null;
        }

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            foreach (var range in ranges)
            {
                var pad = range.Style?.InlineCodePaddingHorizontal ?? 0;
                if (pad <= 0)
                {
                    continue;
                }

                // 该 style range 与当前行的交区间 = 本行内该 range 的片段。
                var fragStart = Math.Max(range.Start, line.Start);
                var fragEnd = Math.Min(range.End, line.TrimEnd);
                if (fragStart >= fragEnd)
                {
                    continue;
                }

                shift ??= new double[styled.Text.Length + 1];
                shift[fragStart] += pad;
                shift[fragEnd] += pad;
            }
        }

        return shift;
    }

    /// <summary>
    /// 把 padShift 并入 xAt 与全部 GlyphRun.Points（同一逻辑坐标系）。
    /// padShift[b] 是 boundary b 处所有 line-fragment pad 之和——行共享边界上
    /// 会同时含上行片段右 pad 与本行片段左 pad，逐行累计时须拆开：
    /// 本行行首只计左 pad（insidePad[b]，覆盖即片段起），
    /// 本行片段末只计右 pad（insidePad[b-1]，前一边界仍被覆盖即片段止）。
    /// </summary>
    private static void ApplyPadShift(double[] padShift, double[] xAt, List<GlyphRun> glyphRuns, List<LineRecord> lines, double[] insidePad)
    {
        // 逐行累计：行首 boundary 是盒原点（不移——左 pad 只把其后的字形/边界右推），
        // 行内其余 boundary 计 padShift（行内 range Start/End 事件），行尾计片段右 pad。
        var lineIndex = 0;
        var cumulative = 0.0;
        for (var i = 0; i < xAt.Length; i++)
        {
            while (lineIndex + 1 < lines.Count && i > lines[lineIndex].TrimEnd)
            {
                lineIndex++;
                cumulative = 0;
            }

            var line = lines[lineIndex];
            double delta;
            if (i == line.Start && i == line.TrimEnd)
            {
                delta = insidePad[i] + (i > 0 ? insidePad[i - 1] : 0);
            }
            else if (i == line.Start)
            {
                delta = insidePad[i];
            }
            else if (i == line.TrimEnd)
            {
                delta = insidePad[i - 1];
            }
            else
            {
                delta = padShift[i];
            }
            cumulative += delta;
            xAt[i] += cumulative;
        }

        // glyph 画布偏移 = 原始字形 X + 其所在字素边界的行内累计 shift（同 xAt 路径）。
        foreach (var run in glyphRuns)
        {
            for (var i = 0; i < run.Points.Length; i++)
            {
                var cluster = run.Clusters[i];
                var li = 0;
                var acc = 0.0;
                for (var b = 0; b <= cluster; b++)
                {
                    while (li + 1 < lines.Count && b > lines[li].TrimEnd)
                    {
                        li++;
                        acc = 0;
                    }

                    var line = lines[li];
                    if (b == line.Start && b == line.TrimEnd)
                    {
                        acc += insidePad[b] + (b > 0 ? insidePad[b - 1] : 0);
                    }
                    else if (b == line.Start)
                    {
                        acc += insidePad[b];
                    }
                    else if (b == line.TrimEnd)
                    {
                        acc += insidePad[b - 1];
                    }
                    else
                    {
                        acc += padShift[b];
                    }
                }

                run.Points[i] = new SKPoint(run.Points[i].X + (float)acc, run.Points[i].Y);
            }
        }
    }

    /// <summary>
    /// 由已存 glyph（id + 段落绝对位置 + cluster）与租用 face 的真实字体度量求墨迹包围盒：
    /// 每 glyph ink rect = SKFont.GetGlyphWidths bounds（相对 glyph 绘制原点）
    /// + (行段落原点 X + Points.X, 行 Baseline + Points.Y)，全 union 后再并入文本块盒
    /// （空文本/纯空白仍保留布局盒尺寸）。一次排版只算一次，栅格化不重新查询字体。
    /// </summary>
    private static Rect ComputeRasterBounds(
        List<GlyphRun> glyphRuns,
        List<LineRecord> lines,
        List<TextLineInfo> lineInfos,
        double[] xAt,
        Size layoutSize)
    {
        // 布局盒是下界之一（空行/无 glyph 时保证尺寸）。
        var left = 0.0;
        var top = 0.0;
        var right = layoutSize.Width;
        var bottom = layoutSize.Height;
        foreach (var run in glyphRuns)
        {
            if (run.GlyphIds.Length == 0)
            {
                continue;
            }

            var runBounds = new SKRect[run.GlyphIds.Length];
            // 同租约 SKFont 查询真实每 glyph ink bounds（相对 glyph 原点；含负 bearing）。
            run.Instance.Font.GetGlyphWidths(run.GlyphIds, widths: [], bounds: runBounds);

            for (var i = 0; i < run.GlyphIds.Length; i++)
            {
                var ink = runBounds[i];
                if (ink.IsEmpty)
                {
                    continue; // 空格/控制符无墨迹
                }

                var lineIndex = GlyphLineIndex(lines, run.Clusters[i]);
                var line = lineInfos[lineIndex];
                // 与栅格化同源：行内段落原点 = Bounds.X - xAt[line.Start]（xAt 段落绝对）。
                var paragraphOriginX = line.Bounds.X - xAt[lines[lineIndex].Start];
                var gx = paragraphOriginX + run.Points[i].X;
                var gy = line.Baseline + run.Points[i].Y;
                left = Math.Min(left, gx + ink.Left);
                right = Math.Max(right, gx + ink.Right);
                top = Math.Min(top, gy + ink.Top);
                bottom = Math.Max(bottom, gy + ink.Bottom);
            }
        }

        return Rect.FromLtrb(left, top, right, bottom);
    }

    /// <summary>
    /// 为每行生成 caret 子表（行内真实 X）：覆盖该行 [Start,End] 的 stop；
    /// 软换行共享 offset 在上行取行右缘、在下行取 OffsetX；硬换行后的行首 offset 只归下行。
    /// </summary>
    private static LineCarets[] BuildLineCarets(
        List<LineRecord> lines,
        List<TextLineInfo> lineInfos,
        int[] caretStops,
        double[] caretXs,
        string text)
    {
        var result = new LineCarets[lines.Count];
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var info = lineInfos[lineIndex];
            var stops = new List<int>();
            var xs = new List<double>();
            for (var i = 0; i < caretStops.Length; i++)
            {
                var stop = caretStops[i];
                if (stop < line.Start || stop > line.End)
                {
                    continue;
                }

                var isHardBreakBeforeStop = stop > 0 && Graphemes.IsLineBreak(text[stop - 1]);
                var isSharedSoftWrap = stop == line.End
                    && lineIndex + 1 < lines.Count
                    && stop == lines[lineIndex + 1].Start
                    && !isHardBreakBeforeStop;
                var isSharedSoftWrapStart = stop == line.Start
                    && lineIndex > 0
                    && stop == lines[lineIndex - 1].End
                    && !isHardBreakBeforeStop;
                var isPostHardBreakStart = stop == line.Start
                    && lineIndex > 0
                    && stop == lines[lineIndex - 1].End
                    && isHardBreakBeforeStop;

                if (isSharedSoftWrap)
                {
                    // 上行末共享 offset：真实 X = 该行内容右缘（行 OffsetX + 内容宽）。
                    stops.Add(stop);
                    xs.Add(info.Bounds.Right);
                }
                else if (isSharedSoftWrapStart || isPostHardBreakStart)
                {
                    // 下行首（软换行共享 offset 下行归属 / 硬换行后的行首）：真实 X = 行 OffsetX。
                    stops.Add(stop);
                    xs.Add(info.Bounds.X);
                }
                else
                {
                    stops.Add(stop);
                    xs.Add(caretXs[i]);
                }
            }

            result[lineIndex] = new LineCarets(stops, xs);
        }

        return result;
    }

    /// <summary>逐行 metrics：行高 = max(显式 LineHeight, 行内 max(ascent)+max(descent)+2×max(vPad))；
    /// baseline 按不含 vPad 的字高定位（vPad 只抬高行盒、不改字形基线）。</summary>
    private List<LineMetrics> ComputeLineMetrics(List<LineRecord> lines, List<FontRun> fontRuns, StyledText styled, RichTextLayoutRequest request)
    {
        var result = new List<LineMetrics>(lines.Count);
        var runIndex = 0;
        var rangeIndex = 0;
        var ranges = styled.Ranges;
        foreach (var line in lines)
        {
            var maxAscent = 0.0;
            var maxDescent = 0.0;
            var maxVPad = 0.0;
            var any = false;
            while (runIndex < fontRuns.Count && fontRuns[runIndex].End <= line.Start)
            {
                runIndex++;
            }

            for (var i = runIndex; i < fontRuns.Count && fontRuns[i].Start < line.TrimEnd; i++)
            {
                var metrics = fontRuns[i].Instance.Font.Metrics;
                maxAscent = Math.Max(maxAscent, Math.Max(0.1, -metrics.Ascent));
                maxDescent = Math.Max(maxDescent, Math.Max(0.1, metrics.Descent));
                any = true;
            }

            while (rangeIndex < ranges.Count && ranges[rangeIndex].End <= line.Start)
            {
                rangeIndex++;
            }

            for (var i = rangeIndex; i < ranges.Count && ranges[i].Start < line.TrimEnd; i++)
            {
                var vPad = ranges[i].Style?.InlineCodePaddingVertical ?? 0;
                if (vPad > maxVPad)
                {
                    maxVPad = vPad;
                }
            }

            if (!any)
            {
                var metrics = _fonts.GetFontInstance(request.ParagraphFont).Font.Metrics;
                maxAscent = Math.Max(0.1, -metrics.Ascent);
                maxDescent = Math.Max(0.1, metrics.Descent);
            }
            var fontHeight = maxAscent + maxDescent;
            var paddedHeight = fontHeight + (2 * maxVPad);
            var height = request.LineHeight > 0 ? Math.Max(request.LineHeight, paddedHeight) : Math.Max(1.0, paddedHeight);
            // vPad 上下对称分在字形上下（top+bottom 均可见留白，非全堆 bottom）：
            // baseline 仍按 (height - fontHeight)/2 + ascent 定位，即字形在行盒内居中。
            var baseline = (height - fontHeight) / 2 + maxAscent;
            result.Add(new LineMetrics(height, baseline));
        }

        return result;
    }

    /// <summary>
    /// face run 按行切成 canonical segment。face run 不跨模型端点，但**可能跨行**（软换行点在
    /// run 中间）——此时按行切成两个 canonical segment，各取该行覆盖的文本区间。
    /// </summary>
    private static List<RichTextSegment> BuildCanonicalSegments(
        List<LineRecord> lines,
        List<TextLineInfo> lineInfos,
        List<FontRun> fontRuns,
        double[] xAt,
        double[]? insidePad,
        StyledText styled)
    {
        var result = new List<RichTextSegment>();
        var runIndex = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var info = lineInfos[i];
            var offsetX = info.Bounds.X;
            var baseline = info.Baseline;
            while (runIndex < fontRuns.Count && fontRuns[runIndex].End <= line.Start)
            {
                runIndex++;
            }

            for (var r = runIndex; r < fontRuns.Count && fontRuns[r].Start < line.TrimEnd; r++)
            {
                var run = fontRuns[r];
                var start = Math.Max(run.Start, line.Start);
                var end = Math.Min(run.End, line.TrimEnd);
                if (start >= end)
                {
                    continue;
                }
                // inset = 本段自身 style pad，仅当 start/end 恰为片段边界：
                // 片段边界 = 行首/TrimEnd 或该段模型段的 Start/End（style range 边界）。
                // padShift[b] 不能直接用——相邻行片段在同一 boundary 各计一次会翻倍。
                var modelSeg = styled.Segments[ModelSegmentAt(styled, start)];
                var segPad = modelSeg.FontSignature.InlineCodePaddingHorizontal ?? 0;
                var insetLeft = segPad > 0 && (start == line.Start || start == modelSeg.Start) ? segPad : 0;
                var insetRight = segPad > 0 && (end == line.TrimEnd || end == modelSeg.End) ? segPad : 0;
                // Width = 盒右缘 − 字形起点：xAt[end] 的累计 shift 含本片段右 pad
                // 与下一片段（相邻 range 起点或下行续片段）的左 pad——只保留本片段
                // 右 pad，下一片段的左 pad 属于下一盒（否则相邻盒重叠一个 pad）。
                var nextLeft = NextFragmentLeftPad(styled, line, end, insidePad);
                result.Add(new RichTextSegment(
                    i, start, end, run.FontId,
                    offsetX + (xAt[start] - xAt[line.Start]) + (insidePad?[line.Start] ?? 0),
                    xAt[end] - xAt[start] - nextLeft,
                    baseline,
                    insetLeft,
                    insetRight));
            }
        }

        return result;
    }

    /// <summary>
    /// 边界 <paramref name="end"/> 处属于"下一片段"的左 pad：
    /// 下一行续片段（end == line.TrimEnd 且被 padded range 覆盖）或同行内
    /// 恰有 padded range 以 end 为 Start——该 pad 进 advance 但不属于本段盒宽。
    /// </summary>
    private static double NextFragmentLeftPad(StyledText styled, LineRecord line, int end, double[]? insidePad)
    {
        if (insidePad is null || insidePad[end] <= 0)
        {
            return 0;
        }

        if (end == line.TrimEnd)
        {
            return insidePad[end];
        }

        foreach (var range in styled.Ranges)
        {
            if (range.Start == end && (range.Style?.InlineCodePaddingHorizontal ?? 0) > 0)
            {
                return range.Style!.InlineCodePaddingHorizontal!.Value;
            }
        }

        return 0;
    }

    /// <summary>该 UTF-16 偏移所在的模型段下标（StyledText.Segments 升序，二分定位）。</summary>
    private static int ModelSegmentAt(StyledText styled, int offset)
    {
        var segments = styled.Segments;
        var low = 0;
        var high = segments.Count - 1;
        var candidate = 0;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (segments[mid].Start <= offset)
            {
                candidate = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return candidate;
    }

    // —— 与 TextLayoutBuilder 同域的工具（私有复制；不公开第二套排版 API）。 ——

    private static FontDescription ResolveFont(FontDescription paragraph, StyledTextFontSignature signature)
        => new(
            signature.Family ?? paragraph.Family,
            Math.Max(0.1, signature.Size ?? paragraph.Size),
            signature.Weight ?? paragraph.Weight,
            signature.Italic ?? paragraph.Italic);

    /// <summary>
    /// 按（请求字体, 实际 face 实例引用）分配 resolved face 身份下标：
    /// 同一底层 face 对象同一身份（<see cref="ResolvedFontIdentity.FaceId"/> 稳定唯一）；
    /// 主 face 与回退 face 即使同 family displayname 也不合并。
    /// </summary>
    private static int FontIdFor(List<ResolvedFontIdentity> fonts, Dictionary<FaceKey, int> ids, FontDescription requested, FontInstance instance)
    {
        var key = new FaceKey(requested, instance);
        if (ids.TryGetValue(key, out var id))
        {
            return id;
        }

        id = fonts.Count;
        fonts.Add(ResolvedFontIdentity.Create(requested, instance.Typeface.FamilyName ?? requested.Family.DisplayName, ResolvedFontIdentity.NextFaceId()));
        ids[key] = id;
        return id;
    }

    /// <summary>face 身份键：请求字体值相等 + face 实例引用相等。</summary>
    private readonly record struct FaceKey(FontDescription Requested, FontInstance Instance);

    /// <summary>码点的 Unicode script（HarfBuzz 内置数据库；不手写码表）。</summary>
    private static HarfBuzzSharp.Script UnicodeScriptOf(int codepoint)
        => HarfBuzzSharp.UnicodeFunctions.Default.GetScript(codepoint);

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

    private static List<LineRecord> BuildLines(
        RichTextLayoutRequest request,
        string text,
        int[] graphemeStarts,
        int[] trimBoundaries,
        double[] xAt,
        List<Paragraph> paragraphs,
        double[]? insidePad,
        double[]? cumPadAt)
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
                    if (MeasuredWidth(text, graphemeStarts, trimBoundaries, xAt, insidePad, cumPadAt, lineStart, cursor + 1) > maxWidth)
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

    private static double MeasuredWidth(
        string text,
        int[] graphemeStarts,
        int[] trimBoundaries,
        double[] xAt,
        double[]? insidePad,
        double[]? cumPadAt,
        int lineStart,
        int boundaryIndex)
    {
        var boundary = graphemeStarts[boundaryIndex];
        var trimmed = Math.Max(lineStart, graphemeStarts[trimBoundaries[boundaryIndex]]);
        var width = xAt[trimmed] - xAt[lineStart];
        if (insidePad is not null && cumPadAt is not null)
        {
            // 行首续片段左 pad（行首被 padding 区间覆盖时计一次）+
            // (lineStart, trimmed] 内的 range Start/End pad 事件 +
            // 行尾若仍被区间覆盖，行尾 fragment 的右 pad。
            width += insidePad[lineStart]
                + (cumPadAt[trimmed] - cumPadAt[lineStart])
                + insidePad[trimmed];
        }

        return width;
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

    /// <summary>
    /// glyph 归属行：半开区间 [line.Start, line.End)——与绘制时 segment.Start &lt;= cluster &lt; segment.End
    /// 同源。软换行共享 offset 的下行行首 glyph 归下行（<see cref="LineIndexForStop"/> 是 caret
    /// 默认上行语义，不能用于墨迹定位）。trimmed 行尾空白不产生墨迹（空格 ink 为空已跳过）。
    /// </summary>
    private static int GlyphLineIndex(List<LineRecord> lines, int cluster)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (cluster >= lines[i].Start && cluster < lines[i].End)
            {
                return i;
            }
        }

        // cluster 落在行 End 上只可能是末行末端（末行 End == text.Length 时 cluster 已到末行）。
        return lines.Count - 1;
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

    // —— 数据结构 ——

    /// <summary>一段同一 resolved 字体 + face 的连续文本（模型段内可能因回退再细分）。</summary>
    private sealed class FontRun
    {
        public FontRun(int start, int end, int fontId, FontInstance instance)
        {
            Start = start;
            End = end;
            FontId = fontId;
            Instance = instance;
        }

        public int Start { get; }

        public int End { get; }

        public int FontId { get; }

        /// <summary>排版使用的字体实例；存活由 backend 的租约保证（不另持引用计数的裸引用）。</summary>
        public FontInstance Instance { get; }
    }

    private readonly record struct LineMetrics(double Height, double Baseline);

    private readonly record struct Paragraph(int Start, int ContentEnd, int End);

    private sealed class LineRecord
    {
        public int Start { get; init; }

        public int End { get; set; }

        public int TrimEnd { get; set; }
    }
}

/// <summary>
/// 一次 face-run shaping 的持久结果：glyph id + 相对段落起点的绝对偏移（含字距换算）
/// + 每个 glyph 的 cluster（UTF-16 偏移）+ face 租约。栅格化按该数组直绘，不重新 shape。
/// </summary>
internal sealed class GlyphRun
{
    public GlyphRun(int start, int end, FontInstanceLease lease, FontInstance instance, ushort[] glyphIds, SKPoint[] points, int[] clusters)
    {
        Start = start;
        End = end;
        Lease = lease;
        Instance = instance;
        GlyphIds = glyphIds;
        Points = points;
        Clusters = clusters;
    }

    public int Start { get; }

    public int End { get; }

    /// <summary>钉住 <see cref="Instance"/> 底层 SKFont/SKShaper/SKTypeface 的租约。</summary>
    public FontInstanceLease Lease { get; }

    /// <summary>排版时使用的字体实例（租约存活期内有效）。</summary>
    public FontInstance Instance { get; }

    /// <summary>HarfBuzz 输出的字形 id（字体内部索引）。</summary>
    public ushort[] GlyphIds { get; }

    /// <summary>相对段落内容起点的 glyph 偏移（x 含 advance 累加与字距；y 为 Y 轴向下偏移）。</summary>
    public SKPoint[] Points { get; }

    /// <summary>每个 glyph 的源文本 cluster（绝对 UTF-16 偏移，字素边界）。</summary>
    public int[] Clusters { get; }
}

/// <summary>
/// 富文本排版的后端负载：shape 子段 glyph 数据（各段自含 face 租约）。
/// <see cref="RichLayoutCore"/> 引用归零时归还全部租约；不持 <see cref="StyledText"/>/payload。
/// </summary>
internal sealed class RichTextBackend : IRichTextLayoutBackend
{
    public RichTextBackend(List<GlyphRun> glyphRuns, double[] xAt)
    {
        GlyphRuns = glyphRuns;
        XAt = xAt;
    }

    /// <summary>按文本区间排序的 face-run glyph 序列。</summary>
    public List<GlyphRun> GlyphRuns { get; }

    /// <summary>各字素边界的段落绝对 X（供栅格化把 run 起点换算为画布 X）。</summary>
    public double[] XAt { get; }

    /// <summary>归还全部租约（幂等：<see cref="FontInstanceLease.Dispose"/> 自身幂等）。</summary>
    public void Dispose()
    {
        foreach (var run in GlyphRuns)
        {
            run.Lease.Dispose();
        }
    }
}
