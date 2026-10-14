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
    /// <summary>构造排版结果。索引必须落在 [0, Text.Length] 且严格递增；首个为 0、末个为 Text.Length。</summary>
    public TextLayoutResult(
        string text,
        FontDescription font,
        double pixelScale,
        Size size,
        IReadOnlyList<TextLineInfo> lines,
        IReadOnlyList<int> caretStops,
        IReadOnlyList<double> caretXs,
        bool isTrimmed = false)
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
    }

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
    /// 命中测试：返回位置最近的字素边界索引。用于点击定位光标与拖选。
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
    /// 把排版结果栅格化为文本遮罩（<see cref="PixelFormat.Alpha8"/>），尺寸为逻辑尺寸 × <see cref="TextLayoutResult.PixelScale"/>。
    /// </summary>
    PixelBuffer Rasterize(TextLayoutResult layout);

    /// <summary>当前缓存的排版结果数量。</summary>
    int CachedLayoutCount { get; }

    /// <summary>当前缓存的栅格化结果数量。</summary>
    int CachedRasterCount { get; }

    /// <summary>排版缓存容量上限；超出后按最近最少使用淘汰。</summary>
    int LayoutCacheCapacity { get; set; }

    /// <summary>清空全部缓存（DPI 或字体变化时调用）。</summary>
    void ClearCaches();
}
