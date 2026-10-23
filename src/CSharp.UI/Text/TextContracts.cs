namespace CSharp.UI;

/// <summary>一次文本排版请求。宽度为逻辑像素；<see cref="PixelScale"/> 用于栅格化时的物理分辨率。</summary>
public sealed record TextLayoutRequest(string Text)
{
    /// <summary>字体描述；默认使用 <see cref="FontDescription.Default"/>。</summary>
    public FontDescription Font { get; init; } = FontDescription.Default;

    /// <summary>可用宽度上限（逻辑像素）；<see cref="double.PositiveInfinity"/> 表示不换行也不省略。</summary>
    public double MaxWidth { get; init; } = double.PositiveInfinity;

    /// <summary>最大行数；首版 Text 默认为 1 行以外的无限行。</summary>
    public int MaxLines { get; init; } = int.MaxValue;

    /// <summary>换行方式。</summary>
    public TextWrapping Wrapping { get; init; } = TextWrapping.NoWrap;

    /// <summary>超出时的省略方式。</summary>
    public TextTrimming Trimming { get; init; } = TextTrimming.None;

    /// <summary>水平对齐。</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>行高（逻辑像素）；0 表示使用字体默认行高。</summary>
    public double LineHeight { get; init; }

    /// <summary>DPI 缩放（逻辑像素 → 物理像素）。参与缓存键。</summary>
    public double PixelScale { get; init; } = 1.0;

    /// <summary>字距调整（逻辑像素），首版供测试与特殊场景使用。</summary>
    public double LetterSpacing { get; init; }

    /// <summary>
    /// 编辑排版模式（多行编辑控件）：true 时生成行内 caret 表（软换行共享 offset 在上下两行各产真实坐标）、
    /// 末尾换行生成尾随空段、caret X 按未裁剪内容宽度（行尾空白推进）、不做省略/截行。
    /// 默认 false（显示路径）；<c>Text</c> 标签与 <c>TextField</c> 永不设置，行为与此前完全一致。
    /// </summary>
    public bool Editing { get; init; }
}

/// <summary>一个 caret 位置的文档 offset、X 与所属视觉行；用于编辑排版模式的行内 caret 表。</summary>
/// <param name="Stop">caret stop 的 UTF-16 offset（字素边界）。</param>
/// <param name="X">该行内的真实 X 坐标（逻辑像素，相对文本块左上角，含对齐偏移）。</param>
/// <param name="LineIndex">所属视觉行索引（<see cref="TextLayoutResult.Lines"/> 下标）。</param>
public readonly record struct CaretPosition(int Stop, double X, int LineIndex);

/// <summary>caret 在共享 offset 上的视觉行归属；用于消除“上行末 vs 下行首”歧义。</summary>
public enum TextAffinity
{
    /// <summary>无归属歧义（或按默认行内规则解析）。</summary>
    Unspecified = 0,

    /// <summary>归属上一行末尾。</summary>
    LineEnd = 1,

    /// <summary>归属下一行行首。</summary>
    LineStart = 2,
}

/// <summary>排版后的单行信息。<see cref="Bounds"/> 与 <see cref="Baseline"/> 相对文本块左上角。</summary>
public sealed record TextLineInfo(int Start, int End, Rect Bounds, double Baseline)
{
    /// <summary>该行包含的 UTF-16 字符数。</summary>
    public int Length => End - Start;
}

/// <summary>
/// 排版结果：尺寸、行、字素边界与光标位置映射。
/// 数据结构自包含，测试替身可以在不加载字体的情况下构造它。
/// </summary>
/// <remarks>
/// <see cref="CaretStops"/> 与 <see cref="CaretXs"/> 一一对应：<c>CaretXs[i]</c> 是 <c>CaretStops[i]</c> 处光标的
/// X 坐标（相对文本块左上角，已包含对齐偏移）。所有索引都是 UTF-16 索引，且只出现在字素边界上。
/// </remarks>
public sealed class TextLayoutResult
{
    /// <summary>
    /// 构造排版结果。索引必须落在 [0, Text.Length] 且严格递增；首个为 0、末个为 Text.Length。
    /// </summary>
    /// <param name="caretsByLine">
    /// 编辑排版模式的行内 caret 表（每行一条 <see cref="CaretPosition"/> 子表）；软换行共享 offset
    /// 在上下两行各产一项真实坐标，硬换行符后 offset 只在下行产生 <see cref="TextAffinity.LineStart"/>。
    /// 省略时首个 <see cref="CaretsByLine"/> 访问按 <paramref name="caretXs"/> 与行归属惰性生成默认表；
    /// 显示路径不为编辑表重复遍历/分配。
    /// </param>
    public TextLayoutResult(
        string text,
        FontDescription font,
        double pixelScale,
        Size size,
        IReadOnlyList<TextLineInfo> lines,
        IReadOnlyList<int> caretStops,
        IReadOnlyList<double> caretXs,
        bool isTrimmed = false,
        double letterSpacing = 0,
        IReadOnlyList<IReadOnlyList<CaretPosition>>? caretsByLine = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(caretStops);
        ArgumentNullException.ThrowIfNull(caretXs);
        if (caretStops.Count != caretXs.Count)
        {
            throw new ArgumentException("caretStops 与 caretXs 数量必须一致。", nameof(caretXs));
        }

        if (caretStops.Count == 0 || caretStops[0] != 0 || caretStops[^1] != text.Length)
        {
            throw new ArgumentException("caretStops 必须从 0 开始并以 Text.Length 结束。", nameof(caretStops));
        }

        for (var i = 1; i < caretStops.Count; i++)
        {
            if (caretStops[i] <= caretStops[i - 1])
            {
                throw new ArgumentException("caretStops 必须严格递增。", nameof(caretStops));
            }
        }

        Text = text;
        Font = font;
        PixelScale = pixelScale <= 0 ? 1 : pixelScale;
        Size = size;
        Lines = lines.Count > 0
            ? lines
            : [new TextLineInfo(0, text.Length, new Rect(0, 0, size.Width, size.Height), size.Height)];
        CaretStops = caretStops;
        CaretXs = caretXs;
        IsTrimmed = isTrimmed;
        LetterSpacing = letterSpacing;
        _explicitCaretsByLine = caretsByLine;
    }

    private readonly IReadOnlyList<IReadOnlyList<CaretPosition>>? _explicitCaretsByLine;
    private volatile IReadOnlyList<IReadOnlyList<CaretPosition>>? _lazyCaretsByLine;

    /// <summary>
    /// 可见文本：即排版与绘制实际使用的文本。
    /// 发生省略或行数截断时，这里是截断后的结果（以 … 结尾），而不是原始完整文本。
    /// </summary>
    public string Text { get; }

    /// <summary>排版使用的字体描述。</summary>
    public FontDescription Font { get; }

    /// <summary>排版使用的 DPI 缩放。</summary>
    public double PixelScale { get; }

    /// <summary>文本块尺寸（逻辑像素）。</summary>
    public Size Size { get; }

    /// <summary>行集合。</summary>
    public IReadOnlyList<TextLineInfo> Lines { get; }

    /// <summary>字素边界上的 UTF-16 索引（升序，含 0 与 Text.Length）。</summary>
    public IReadOnlyList<int> CaretStops { get; }

    /// <summary>与 <see cref="CaretStops"/> 对应的光标 X 坐标。</summary>
    public IReadOnlyList<double> CaretXs { get; }

    /// <summary>是否因为省略而隐藏了部分文本。</summary>
    public bool IsTrimmed { get; }

    /// <summary>
    /// 字距（逻辑像素）：每个字素簇之后附加的水平推进量。
    /// 栅格化必须使用同一数值，否则测量宽度与实际字形间距会不一致。
    /// </summary>
    public double LetterSpacing { get; }

    /// <summary>
    /// 编辑排版模式的行内 caret 表：每行一条 <see cref="CaretPosition"/> 子表；
    /// 软换行共享 offset 在上下两行各产一项真实坐标（上行末 X=行内容宽、下行首 X=OffsetX），
    /// 硬换行符后 offset 只在下行产生 <see cref="TextAffinity.LineStart"/>。
    /// 非编辑请求由构造器按 <see cref="CaretXs"/> 与行归属生成默认表（显示路径）。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<CaretPosition>> CaretsByLine
        => _explicitCaretsByLine ?? (_lazyCaretsByLine ??= BuildDefaultCaretsByLine());

    /// <summary>
    /// 按 <paramref name="affinity"/> 归属返回指定索引处的光标 X 坐标；
    /// 软换行共享 offset 消歧（<see cref="TextAffinity.LineEnd"/> 取上行末、<see cref="TextAffinity.LineStart"/> 取下行首），
    /// 非歧义位置忽略 affinity。
    /// </summary>
    public double GetCaretX(int index, TextAffinity affinity)
    {
        var snapped = SnapToCaretStop(index);
        var lineIndex = ResolveLineIndex(snapped, affinity, out var x);
        return x ?? GetCaretX(snapped);
    }

    /// <summary>返回指定索引在 <paramref name="affinity"/> 归属下的视觉行索引。</summary>
    public int LineIndexForIndex(int index, TextAffinity affinity)
    {
        var snapped = SnapToCaretStop(index);
        return ResolveLineIndex(snapped, affinity, out _);
    }

    /// <summary>
    /// 按 Lines 二分与行内 caret 表定位 <paramref name="snapped"/> 在 <paramref name="affinity"/> 下的行；
    /// 命中行内共享 offset 时经 <paramref name="x"/> 输出该行真实 X，未命中输出 null。
    /// 非歧义 offset 忽略 affinity，直接归 <c>LineIndexForStop</c>。
    /// </summary>
    private int ResolveLineIndex(int snapped, TextAffinity affinity, out double? x)
    {
        x = null;
        var primary = LineIndexForStop(snapped);

        // 硬换行后的行首 offset（text[stop-1] 是换行符）：唯一归属下行，任何 affinity 都解析到它。
        // primary 此时是下行（LineIndexForStop 取 latest Start<=stop → 共享/后继 offset 归下行）；
        // 下行 CBL 含该 stop 的 LineStart 归属 caret。
        var isPostBreak = snapped > 0 && Text[snapped - 1] is '\n' or '\r';
        if (isPostBreak)
        {
            var hit = FindCaretX(primary, snapped);
            if (hit.HasValue)
            {
                x = hit;
            }
            return primary;
        }

        // 软换行共享 offset（Lines[primary-1].End == snapped == Lines[primary].Start，且 text[stop-1] 非换行）：
        // 上行产 LineEnd（X=内容宽右缘）、下行产 LineStart（X=OffsetX）；Unspecified 取上行（旧单 X 语义）。
        var isShared = primary > 0
            && snapped == Lines[primary].Start
            && snapped == Lines[primary - 1].End;
        if (isShared)
        {
            var target = affinity switch
            {
                TextAffinity.LineStart => primary,          // 下行首
                _ => primary - 1,                            // 上行末（LineEnd 与 Unspecified 默认）
            };
            var hit = FindCaretX(target, snapped);
            if (hit.HasValue)
            {
                x = hit;
            }
            return target;
        }

        // 非共享/非歧义 offset：忽略 affinity，直接归 primary。
        return primary;
    }

    /// <summary>在指定行的 caret 子表内按 stop 查真实 X；未命中返回 null。</summary>
    private double? FindCaretX(int lineIndex, int stop)
    {
        var carets = CaretsByLine[Math.Clamp(lineIndex, 0, CaretsByLine.Count - 1)];
        var found = FindStopInCarets(carets, stop);
        return found >= 0 ? carets[found].X : null;
    }

    private static int FindStopInCarets(IReadOnlyList<CaretPosition> carets, int stop)
    {
        var low = 0;
        var high = carets.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var s = carets[mid].Stop;
            if (s == stop)
            {
                return mid;
            }

            if (s < stop)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }

    /// <summary>返回指定视觉行的行首 caret stop（该行首个 <see cref="CaretPosition"/> 的 offset）。</summary>
    public int LineStartIndex(int lineIndex)
    {
        var carets = CaretsByLine[Math.Clamp(lineIndex, 0, CaretsByLine.Count - 1)];
        return carets.Count > 0 ? carets[0].Stop : Lines[Math.Clamp(lineIndex, 0, Lines.Count - 1)].Start;
    }

    /// <summary>
    /// 返回指定视觉行的行尾 caret stop：该行换行符前的最后一个 caret offset。
    /// 硬换行行的 terminator 不在行内可停留位置；terminator 后 offset 属下行 <see cref="TextAffinity.LineStart"/>。
    /// </summary>
    public int LineEndIndex(int lineIndex)
    {
        var carets = CaretsByLine[Math.Clamp(lineIndex, 0, CaretsByLine.Count - 1)];
        return carets.Count > 0 ? carets[^1].Stop : Lines[Math.Clamp(lineIndex, 0, Lines.Count - 1)].End;
    }

    /// <summary>
    /// 指定视觉行内按 X 命中最近 caret；返回 caret stop 的 UTF-16 offset，
    /// 共享 offset 的归属经 <paramref name="affinity"/> 返回（X 更靠行尾→<see cref="TextAffinity.LineEnd"/>，行首→<see cref="TextAffinity.LineStart"/>）。
    /// </summary>
    public int HitTestLine(int lineIndex, double x, out TextAffinity affinity)
    {
        var carets = CaretsByLine[Math.Clamp(lineIndex, 0, CaretsByLine.Count - 1)];
        if (carets.Count == 0)
        {
            affinity = TextAffinity.Unspecified;
            return LineStartIndex(lineIndex);
        }

        var best = carets[0];
        var bestDistance = double.PositiveInfinity;
        foreach (var caret in carets)
        {
            var distance = Math.Abs(caret.X - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = caret;
            }
        }

        var stop = best.Stop;
        var lineIdx = Math.Clamp(lineIndex, 0, Lines.Count - 1);

        // 软换行共享 offset：命中行内该 stop 的真实坐标归属命中行自身——上行→LineEnd，下行→LineStart。
        if (lineIdx + 1 < Lines.Count
            && stop == Lines[lineIdx].End
            && stop == Lines[lineIdx + 1].Start
            && (stop == 0 || Text[stop - 1] is not ('\n' or '\r')))
        {
            affinity = TextAffinity.LineEnd;
        }
        else if (lineIdx > 0
            && stop == Lines[lineIdx].Start
            && stop == Lines[lineIdx - 1].End
            && (stop == 0 || Text[stop - 1] is not ('\n' or '\r')))
        {
            affinity = TextAffinity.LineStart;
        }
        else if (stop > 0 && stop == Lines[lineIdx].Start
            && stop < Text.Length && Text[stop - 1] is '\n' or '\r')
        {
            // 硬换行后的行首 offset：归属下一行。
            affinity = TextAffinity.LineStart;
        }
        else
        {
            affinity = TextAffinity.Unspecified;
        }

        return best.Stop;
    }

    /// <summary>
    /// 按 <paramref name="affinity"/> 归属返回指定索引处的行内 caret 矩形。
    /// </summary>
    public Rect GetCaretBounds(int index, TextAffinity affinity, double width = 1)
    {
        var snapped = SnapToCaretStop(index);
        var lineIndex = ResolveLineIndex(snapped, affinity, out var x);
        var line = Lines[Math.Clamp(lineIndex, 0, Lines.Count - 1)];
        var caretX = x ?? GetCaretX(snapped);
        return new Rect(caretX, line.Bounds.Y, width, line.Bounds.Height);
    }

    /// <summary>
    /// 命中测试：先按 Y 定行，再在该行 caret 子表比 X；返回 caret stop 与共享 offset 的归属。
    /// </summary>
    public int HitTestIndex(Point position, out TextAffinity affinity)
    {
        var lineIndex = LineIndexForPosition(position);
        return HitTestLine(lineIndex, position.X, out affinity);
    }

    /// <summary>
    /// 返回 [start, end) 范围内文本的逐行矩形列表（编辑排版模式选择高亮）。
    /// 行间无中间空白填充：每行矩形按该行 caret 子表 clip [line.Start, LineEndIndex(line)] 的局部真实 X；
    /// 选区覆盖硬换行符时在该行行尾追加零宽 newline marker；空行被覆盖时给全行高零宽 marker；
    /// <paramref name="end"/> 恰为下一行 Start 不误选该下行；空范围不产 marker。
    /// </summary>
    public IReadOnlyList<Rect> GetRangeRects(int start, int end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var snappedStart = SnapToCaretStop(start);
        var snappedEnd = SnapToCaretStop(end, roundUp: true);
        var rects = new List<Rect>();

        if (snappedStart == snappedEnd)
        {
            return rects; // 空选区不产任何 rect/marker
        }

        for (var lineIndex = 0; lineIndex < Lines.Count; lineIndex++)
        {
            var line = Lines[lineIndex];
            var lineEndBound = LineEndIndex(lineIndex);   // terminator 前的行尾 stop（硬换行时 < line.End）
            var contentStart = line.Start;                // 内容区起点
            var contentEnd = lineEndBound;                // 内容区终点（不含 terminator）
            var terminatorStart = lineEndBound;           // terminator 区起点（硬换行时到 line.End）
            var terminatorEnd = line.End;                 // terminator 区终点（软换行时 == lineEndBound，零宽）

            // —— 内容区 ∩ selection：独立产内容 rect。空内容区（contentStart==contentEnd）无 rect。
            var selStart = Math.Max(snappedStart, contentStart);
            var selEnd = Math.Min(snappedEnd, contentEnd);
            if (selStart < selEnd)
            {
                var left = LocalCaretXInLine(lineIndex, selStart) ?? line.Bounds.X;
                var right = LocalCaretXInLine(lineIndex, selEnd) ?? line.Bounds.Right;
                if (right < left)
                {
                    (left, right) = (right, left);
                }

                rects.Add(new Rect(left, line.Bounds.Y, Math.Max(0, right - left), line.Bounds.Height));
            }

            // —— terminator 区 ∩ selection：硬换行（terminatorEnd > terminatorStart 且 text[end-1] 是换行）
            //   独立产零宽 marker（绘制层决定可见宽）。软换行/无 terminator 不产。
            if (terminatorEnd > terminatorStart
                && Text[terminatorEnd - 1] is '\n' or '\r'
                && snappedStart < terminatorEnd
                && snappedEnd > terminatorStart)
            {
                var edge = LocalCaretXInLine(lineIndex, terminatorStart) ?? line.Bounds.Right;
                rects.Add(new Rect(edge, line.Bounds.Y, 0, line.Bounds.Height));
            }

            // —— 全空行（无内容区、无 terminator 区覆盖外的可停留位置）：Skia 中间空行
            //   [s,e) 含 \n 时走上面 terminator 分支已产 marker；纯空段（start==end 且无 terminator）
            //   被覆盖时给零宽全高 marker。
            if (contentStart == contentEnd && terminatorStart == terminatorEnd
                && line.Start >= snappedStart && line.Start < snappedEnd)
            {
                rects.Add(new Rect(line.Bounds.X, line.Bounds.Y, 0, line.Bounds.Height));
            }
        }

        return rects;
    }

    /// <summary>在指定行的 caret 子表内查 stop 的真实 X（该行 clip 范围内局部坐标）；未命中返回 null。</summary>
    private double? LocalCaretXInLine(int lineIndex, int stop)
        => FindCaretX(lineIndex, stop);

    /// <summary>按 Lines.Start 二分定位含 stop 的行；stop 在相邻行边界（End==下一行 Start）归上行。</summary>
    private int LineIndexForStop(int stop)
    {
        var low = 0;
        var high = Lines.Count - 1;
        var candidate = 0;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (Lines[mid].Start <= stop)
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

    private IReadOnlyList<IReadOnlyList<CaretPosition>> BuildDefaultCaretsByLine()
    {
        var result = new List<IReadOnlyList<CaretPosition>>(Lines.Count);
        for (var lineIndex = 0; lineIndex < Lines.Count; lineIndex++)
        {
            var line = Lines[lineIndex];
            var carets = new List<CaretPosition>();
            for (var i = 0; i < CaretStops.Count; i++)
            {
                var stop = CaretStops[i];
                if (stop < line.Start || stop > line.End)
                {
                    continue;
                }

                // 硬换行后的行首 offset（text[stop-1] 是换行且 stop==下一行 Start）：不归上行。
                if (stop == line.End && stop > 0 && Text[stop - 1] is '\n' or '\r'
                    && lineIndex + 1 < Lines.Count && stop == Lines[lineIndex + 1].Start)
                {
                    continue;
                }

                carets.Add(new CaretPosition(stop, CaretXs[i], lineIndex));
            }

            result.Add(carets);
        }

        return result;
    }


    /// <summary>把任意 UTF-16 索引吸附到最近的合法字素边界；<paramref name="roundUp"/> 为 true 时向上取。</summary>
    public int SnapToCaretStop(int index, bool roundUp = false)
    {
        var clamped = Math.Clamp(index, 0, Text.Length);
        var found = FindStop(CaretStops, clamped);
        if (found >= 0)
        {
            return CaretStops[found];
        }

        var insertion = ~found;
        if (!roundUp)
        {
            return CaretStops[Math.Max(0, insertion - 1)];
        }

        return CaretStops[Math.Min(CaretStops.Count - 1, insertion)];
    }

    /// <summary>在升序索引列表中执行二分查找；未找到时返回按位取反的插入位置。</summary>
    internal static int FindStop(IReadOnlyList<int> values, int target)
    {
        var low = 0;
        var high = values.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var value = values[middle];
            if (value == target)
            {
                return middle;
            }

            if (value < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    /// <summary>返回包含指定位置的行的索引；超出范围时取最近的行。</summary>
    public int LineIndexForPosition(Point position)
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (position.Y >= line.Bounds.Top && position.Y < line.Bounds.Bottom)
            {
                return i;
            }
        }

        return position.Y < Lines[0].Bounds.Top ? 0 : Lines.Count - 1;
    }

    /// <summary>
    /// 命中测试（显示路径）：先按 Y 定行，再在该行 caret 表比 X 取最近字素边界。
    /// 软换行共享 offset 归属按行内规则解析（单 X 列表）；编辑控件应改用
    /// <see cref="HitTestIndex(Point, out TextAffinity)"/> 以解析行首/行尾歧义。
    /// </summary>
    public int HitTestIndex(Point position)
    {
        var lineIndex = LineIndexForPosition(position);
        var best = CaretStops[0];
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < CaretStops.Count; i++)
        {
            var caretX = CaretXs[i];
            if (!IsOnLine(i, lineIndex))
            {
                continue;
            }

            var distance = Math.Abs(caretX - position.X);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = CaretStops[i];
            }
        }

        return best;
    }

    /// <summary>返回指定字素边界索引处的光标 X 坐标；索引不在边界上时向下吸附。</summary>
    public double GetCaretX(int index) => CaretXs[IndexOfCaretStop(SnapToCaretStop(index))];

    /// <summary>返回指定索引所在行的纵向信息。</summary>
    public TextLineInfo GetLineForIndex(int index)
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            if (index >= Lines[i].Start && index <= Lines[i].End)
            {
                return Lines[i];
            }
        }

        return Lines[^1];
    }

    /// <summary>返回 [start, end) 范围内文本的包围矩形，用于选择高亮；范围为空时返回零宽矩形。</summary>
    public Rect GetRangeBounds(int start, int end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var snappedStart = SnapToCaretStop(start);
        var snappedEnd = SnapToCaretStop(end, roundUp: true);
        var result = Rect.Empty;
        var any = false;
        foreach (var line in Lines)
        {
            var lineStart = Math.Max(snappedStart, line.Start);
            var lineEnd = Math.Min(snappedEnd, line.End);
            var touchesLine = snappedStart <= line.End && snappedEnd >= line.Start && snappedStart != snappedEnd;
            if (!touchesLine)
            {
                continue;
            }

            var left = GetCaretX(lineStart);
            var right = GetCaretX(lineEnd);
            if (right < left)
            {
                (left, right) = (right, left);
            }

            var rect = new Rect(left, line.Bounds.Y, Math.Max(0, right - left), line.Bounds.Height);
            result = any ? result.Union(rect) : rect;
            any = true;
        }

        if (!any)
        {
            var line = GetLineForIndex(snappedStart);
            var x = GetCaretX(snappedStart);
            return new Rect(x, line.Bounds.Y, 0, line.Bounds.Height);
        }

        return result;
    }

    /// <summary>返回索引处的行内 caret 矩形（用于光标绘制）。</summary>
    public Rect GetCaretBounds(int index, double width = 1)
    {
        var snapped = SnapToCaretStop(index);
        var line = GetLineForIndex(snapped);
        return new Rect(GetCaretX(snapped), line.Bounds.Y, width, line.Bounds.Height);
    }

    private int IndexOfCaretStop(int index)
    {
        var found = FindStop(CaretStops, index);
        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }

    private bool IsOnLine(int caretIndex, int lineIndex)
    {
        var index = CaretStops[caretIndex];
        var line = Lines[lineIndex];
        return index >= line.Start && index <= line.End;
    }
}

/// <summary>
/// 文本服务契约：排版、测量与 CPU 栅格化。实现位于 <c>CSharp.UI.Skia</c>（SkiaSharp + HarfBuzz）。
/// </summary>
/// <remarks>
/// 所有权：排版结果由调用方持有；栅格化返回的 <see cref="PixelBuffer"/> 由服务缓存或由调用方交给纹理服务后丢弃。
/// 返回类型不暴露 SKTypeface/SKShaper 等后端对象。
/// </remarks>
public interface ITextService
{
    /// <summary>排版文本；相同请求应复用缓存结果。</summary>
    TextLayoutResult Layout(TextLayoutRequest request);

    /// <summary>测量文本尺寸（逻辑像素），等价于 <see cref="Layout"/> 后读取 Size。</summary>
    Size Measure(TextLayoutRequest request);

    /// <summary>返回字体纵向度量（逻辑像素）。Ascent/Descent 为正数；LineGap 为字体真实 leading，允许为 0。</summary>
    FontMetrics GetFontMetrics(FontDescription font);

    /// <summary>
    /// 把排版结果栅格化为白色文字遮罩；等价于 <see cref="Rasterize(TextLayoutResult, Color)"/> 传 <see cref="Color.White"/>。
    /// </summary>
    PixelBuffer Rasterize(TextLayoutResult layout);

    /// <summary>
    /// 按实际文字颜色把排版结果栅格化为文本遮罩（<see cref="PixelFormat.Alpha8"/>），
    /// 尺寸为逻辑尺寸 × <see cref="TextLayoutResult.PixelScale"/>。
    /// </summary>
    /// <param name="layout">排版结果；布局、换行与字形位置必须与测量阶段共用同一个结果。</param>
    /// <param name="color">
    /// 文字颜色。只使用 RGB：栅格化按不透明颜色（alpha=255）进行，透明度由绘制端 tint 统一乘一次，
    /// 避免半透明文字发生双重 alpha 混合。
    /// </param>
    /// <remarks>
    /// <para>
    /// 契约要求：遮罩必须与“用同一排版和同一颜色直接绘制到颜色表面”得到的覆盖率一致。
    /// Skia 的字形遮罩带与绘制颜色相关的 gamma/对比度处理（见 The Raster Tragedy in Skia），
    /// 因此同一排版在不同前景色下可能得到不同边缘值，不能把一份白字遮罩染色后复用于所有颜色。
    /// </para>
    /// <para>
    /// 兼容性：默认实现忽略颜色并返回 <see cref="Rasterize(TextLayoutResult)"/>，使既有外部实现保持可编译、
    /// 并沿用其原有栅格化行为；框架自带的 <c>CSharp.UI.Skia.SkiaTextService</c> 会实现颜色相关版本。
    /// </para>
    /// </remarks>
    PixelBuffer Rasterize(TextLayoutResult layout, Color color) => Rasterize(layout);

    /// <summary>当前缓存的排版结果数量。</summary>
    int CachedLayoutCount { get; }

    /// <summary>当前缓存的栅格化结果数量。</summary>
    int CachedRasterCount { get; }

    /// <summary>排版缓存容量上限；超出后按最近最少使用淘汰。</summary>
    int LayoutCacheCapacity { get; set; }

    /// <summary>清空全部缓存（DPI 或字体变化时调用）。</summary>
    void ClearCaches();
}
