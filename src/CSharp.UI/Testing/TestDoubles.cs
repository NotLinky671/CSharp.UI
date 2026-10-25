using System.Collections.Concurrent;
using System.Globalization;

namespace CSharp.UI.Testing;

/// <summary>
/// 无窗口测试用调度器：记录投递的动作与重绘/唤醒请求，由测试显式执行队列。
/// </summary>
/// <remarks>创建它的线程被视作 UI 线程，便于在测试中验证线程约束。</remarks>
public sealed class HeadlessUiDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private int _redrawRequests;
    private long _nextWakeupTicks = -1;

    /// <inheritdoc />
    public bool IsOnUiThread => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>待执行的投递动作数量。</summary>
    public int PendingCount => _queue.Count;

    /// <summary>累计重绘请求次数。</summary>
    public int RedrawRequestCount => Volatile.Read(ref _redrawRequests);

    /// <summary>最近一次被请求的最早唤醒延迟；未请求过时为 null。</summary>
    public TimeSpan? NextWakeup
    {
        get
        {
            var ticks = Interlocked.Read(ref _nextWakeupTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
    }

    /// <inheritdoc />
    public void RequestRedraw() => Interlocked.Increment(ref _redrawRequests);

    /// <inheritdoc />
    public void RequestWakeup(TimeSpan delay)
    {
        var ticks = delay.Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _nextWakeupTicks);
            if (current >= 0 && current <= ticks)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _nextWakeupTicks, ticks, current) != current);
    }

    /// <summary>在当前线程执行全部已投递动作，返回执行数量。</summary>
    public int RunPending()
    {
        var count = 0;
        while (_queue.TryDequeue(out var action))
        {
            action();
            count++;
        }

        return count;
    }

    /// <summary>清除唤醒请求（模拟一次事件循环迭代消费了定时唤醒）。</summary>
    public void ClearWakeup() => Interlocked.Exchange(ref _nextWakeupTicks, -1);
}

/// <summary>测试用可控时钟。</summary>
public sealed class TestUiClock : IUiClock
{
    /// <summary>当前时间。</summary>
    public TimeSpan Now { get; set; }

    /// <summary>推进时间。</summary>
    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>
/// 测试用文本服务：按等宽字体估算排版结果，不加载任何原生字体库。
/// </summary>
/// <remarks>
/// 通过 <see cref="CharWidthScale"/> 可以让文本“变宽”，用于验证宽度变化触发布局重新测量。
/// </remarks>
public sealed class StubTextService : ITextService, IRichTextService
{
    private readonly LruCache<TextLayoutRequest, TextLayoutResult> _layouts = new(64);
    private readonly LruCache<StubRasterKey, PixelBuffer> _rasters = new(32);
    private int _layoutMisses;
    private int _rasterMisses;

    private double _charWidthScale = 0.6;

    /// <summary>
    /// 由字符宽度比例调整的实测宽度系数（默认 0.6 × 字号）。
    /// 修改会清空缓存，模拟“度量提供方发生变化”的场景。
    /// </summary>
    public double CharWidthScale
    {
        get => _charWidthScale;
        set
        {
            if (Math.Abs(value - _charWidthScale) < 0.0001)
            {
                return;
            }

            _charWidthScale = value;
            ClearCaches();
        }
    }

    /// <inheritdoc />
    public int CachedLayoutCount => _layouts.Count;

    /// <inheritdoc />
    public int CachedRasterCount => _rasters.Count;

    /// <inheritdoc />
    public int LayoutCacheCapacity
    {
        get => _layouts.Capacity;
        set => _layouts.Capacity = value;
    }

    /// <summary>实际执行排版的次数（缓存命中不计数）。</summary>
    public int LayoutMisses => _layoutMisses;

    /// <summary>实际执行栅格化的次数。</summary>
    public int RasterMisses => _rasterMisses;

    /// <inheritdoc />
    public TextLayoutResult Layout(TextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hadValue = _layouts.TryGet(request, out var cached);
        if (hadValue)
        {
            return cached!;
        }

        _layoutMisses++;
        var result = Build(request);
        _layouts.GetOrAdd(request, _ => result, out _);
        return result;
    }

    /// <inheritdoc />
    public Size Measure(TextLayoutRequest request) => Layout(request).Size;

    /// <inheritdoc />
    public FontMetrics GetFontMetrics(FontDescription font)
        => new(font.Size * 0.8, font.Size * 0.2, font.Size * 0.25);

    /// <inheritdoc />
    public PixelBuffer Rasterize(TextLayoutResult layout) => Rasterize(layout, Color.White);

    /// <inheritdoc />
    /// <remarks>与真实实现一致：颜色只以 RGB 参与缓存键，alpha 不参与（由绘制端统一乘一次）。</remarks>
    public PixelBuffer Rasterize(TextLayoutResult layout, Color color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var key = new StubRasterKey(layout, color.WithAlpha(255));
        var hadValue = _rasters.TryGet(key, out var cached);
        if (hadValue)
        {
            return cached!;
        }

        _rasterMisses++;
        var width = Math.Max(1, (int)Math.Ceiling(layout.Size.Width * layout.PixelScale));
        var height = Math.Max(1, (int)Math.Ceiling(layout.Size.Height * layout.PixelScale));
        var buffer = new PixelBuffer(width, height, PixelFormat.Alpha8);
        Array.Fill(buffer.Pixels, (byte)200);
        _rasters.GetOrAdd(key, _ => buffer, out _);
        return buffer;
    }

    /// <inheritdoc />
    public void ClearCaches()
    {
        _layouts.Clear();
        _rasters.Clear();
    }

    /// <summary>替身栅格缓存键：排版结果实例 + 文字颜色 RGB。</summary>
    private readonly record struct StubRasterKey(TextLayoutResult Layout, Color Color);

    private TextLayoutResult Build(TextLayoutRequest request)
    {
        var text = request.Text;
        var glyphWidth = Math.Max(1, request.Font.Size * CharWidthScale) + request.LetterSpacing;
        var lineHeight = request.LineHeight > 0 ? request.LineHeight : request.Font.Size * 1.25;
        var clusters = GraphemeStarts(text);

        var lines = new List<(int Start, int End)>();
        // 编辑模式需要每行的“内容终点”（不含硬换行 terminator），供宽度/ caret X 使用；
        // 非编辑路径 ContentEnd 恒等于 End，行为与之前完全一致。
        var lineContentEnds = new List<int>();
        var isTrimmed = false;

        if (request.Editing)
        {
            // 编辑排版（镜像 TextLayoutBuilder）：硬换行段落的行 End 含 terminator
            // （\r\n 计 2 字符、单独 \r/\n/\u2028/\u2029/\u0085 计 1），contentEnd 不含；
            // 软换行行 End == 下行 Start（共享 offset）；末尾换行补零宽尾随空段。
            var segments = SplitParagraphs(text);
            foreach (var (segStart, segContentEnd, segEnd) in segments)
            {
                var seg = text[segStart..segContentEnd];
                var segLines = request.Wrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(request.MaxWidth)
                    ? WrapClusters(seg, GraphemeStarts(seg), glyphWidth, request.MaxWidth)
                    : [(0, seg.Length)];

                for (var i = 0; i < segLines.Count; i++)
                {
                    var (s, e) = segLines[i];
                    var isLastOfSegment = i == segLines.Count - 1;
                    // 段内最后一行吸收 terminator：End = segEnd（含换行符），ContentEnd = 内容末。
                    var lineEnd = isLastOfSegment ? segEnd : segStart + e;
                    var contentEnd = isLastOfSegment ? segContentEnd : lineEnd;
                    lines.Add((segStart + s, lineEnd));
                    lineContentEnds.Add(contentEnd);
                }
            }

            // 末尾换行 → 尾随空段（零宽）。无文本时 paragraphs 已产一行 [0,0)。
            if (text.Length > 0 && IsLineBreak(text[^1]))
            {
                lines.Add((text.Length, text.Length));
                lineContentEnds.Add(text.Length);
            }
        }
        else if (request.Wrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(request.MaxWidth))
        {
            foreach (var (s, e) in WrapClusters(text, clusters, glyphWidth, request.MaxWidth))
            {
                lines.Add((s, e));
                lineContentEnds.Add(e);
            }
        }
        else
        {
            lines.Add((0, text.Length));
            lineContentEnds.Add(text.Length);
        }

        // 省略：截断可见文本。
        var visibleText = text;
        if (request.Trimming == TextTrimming.Ellipsis && !request.MaxWidth.Equals(double.PositiveInfinity))
        {
            var maxClusters = (int)Math.Floor(Math.Max(0, request.MaxWidth - glyphWidth) / glyphWidth);
            if (clusters.Count - 1 > maxClusters)
            {
                visibleText = string.Concat(text.AsSpan(0, clusters[Math.Max(0, maxClusters)]), "…");
                isTrimmed = true;
                lines.Clear();
                lineContentEnds.Clear();
                lines.Add((0, visibleText.Length));
                lineContentEnds.Add(visibleText.Length);
            }
        }

        if (lines.Count > request.MaxLines && request.MaxLines > 0)
        {
            lines.RemoveRange(request.MaxLines, lines.Count - request.MaxLines);
            lineContentEnds.RemoveRange(request.MaxLines, lineContentEnds.Count - request.MaxLines);
            if (request.Trimming == TextTrimming.Ellipsis)
            {
                isTrimmed = true;
            }
        }

        var visibleClusters = GraphemeStarts(visibleText);
        var stops = new List<int>(visibleClusters);
        if (stops.Count == 0 || stops[^1] != visibleText.Length)
        {
            stops.Add(visibleText.Length);
        }

        double maxWidth = 0;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var (start, _) = lines[lineIndex];
            maxWidth = Math.Max(maxWidth, (lineContentEnds[lineIndex] - start) * glyphWidth);
        }

        var lineInfos = new List<TextLineInfo>(lines.Count);
        var lineOffsets = new List<double>(lines.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var (start, end) = lines[lineIndex];
            var contentWidth = (lineContentEnds[lineIndex] - start) * glyphWidth;
            var offsetX = request.Alignment switch
            {
                TextAlignment.Center => (maxWidth - contentWidth) / 2,
                TextAlignment.Right => maxWidth - contentWidth,
                _ => 0,
            };
            lineOffsets.Add(offsetX);
            var bounds = new Rect(offsetX, lineIndex * lineHeight, contentWidth, lineHeight);
            lineInfos.Add(new TextLineInfo(start, end, bounds, lineIndex * lineHeight + (lineHeight * 0.8)));
        }

        var caretXs = new List<double>(stops.Count);
        List<IReadOnlyList<CaretPosition>>? caretsByLine = request.Editing ? new(lines.Count) : null;

        foreach (var stop in stops)
        {
            // caret X：行内坐标（首个 Start≤stop≤End 的行；共享 offset 归上行，与真实 legacy caretXs 一致）。
            // terminator 内的 stop 夹到内容终点（换行符不算宽度；stub 无 TrimEnd 裁剪）。
            var lineIndex = LineIndexForStop(lines, stop);
            var (start, _) = lines[lineIndex];
            var xStop = Math.Min(stop, lineContentEnds[lineIndex]);
            caretXs.Add(lineOffsets[lineIndex] + ((xStop - start) * glyphWidth));
        }

        if (request.Editing)
        {
            caretsByLine = BuildEditingCaretsByLine(lines, lineContentEnds, lineOffsets, stops, text, glyphWidth);
        }

        var size = new Size(maxWidth, Math.Max(lineHeight, lines.Count * lineHeight));
        return new TextLayoutResult(
            visibleText,
            request.Font,
            request.PixelScale,
            size,
            lineInfos,
            stops,
            caretXs,
            isTrimmed,
            request.LetterSpacing,
            caretsByLine);
    }

    /// <summary>
    /// 把文本按硬换行切成段落 (contentStart, contentEnd, end)：<c>\r\n</c> 作一个 terminator（end 前移 2），
    /// 单独 <c>\r</c>/<c>\n</c>/<c>\u2028</c>/<c>\u2029</c>/<c>\u0085</c> 各作一个 terminator（end 前移 1）；
    /// contentEnd 是 terminator 前的内容终点。与 TextLayoutBuilder 的段落切分同域（同一组换行码点），
    /// 只作 stub 简化模型，不复制第二套字素规则。
    /// </summary>
    private static List<(int ContentStart, int ContentEnd, int End)> SplitParagraphs(string text)
    {
        var result = new List<(int ContentStart, int ContentEnd, int End)>();
        var index = 0;
        while (true)
        {
            var start = index;
            var contentEnd = start;
            while (contentEnd < text.Length && !IsLineBreak(text[contentEnd]))
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

            result.Add((start, contentEnd, end));
            if (end >= text.Length)
            {
                break;
            }

            index = end;
        }

        return result;
    }

    /// <summary>判断码点是否为硬换行符（与真实排版 <c>Graphemes.IsLineBreak</c> 同组码点）。</summary>
    private static bool IsLineBreak(char value) => value is '\n' or '\r' or '\u2028' or '\u2029' or '\u0085';

    /// <summary>编辑排版：为每行生成 caret 子表；软换行共享 offset 在上下行各产真实坐标。</summary>
    private static List<IReadOnlyList<CaretPosition>> BuildEditingCaretsByLine(
        List<(int Start, int End)> lines,
        List<int> lineContentEnds,
        List<double> lineOffsets,
        List<int> stops,
        string text,
        double glyphWidth)
    {
        var result = new List<IReadOnlyList<CaretPosition>>(lines.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var (start, end) = lines[lineIndex];
            var contentEnd = lineContentEnds[lineIndex];
            var carets = new List<CaretPosition>();
            foreach (var stop in stops)
            {
                if (stop < start || stop > end)
                {
                    continue;
                }

                var isHardBreakBeforeStop = stop > 0 && IsLineBreak(text[stop - 1]);
                var isSoftWrapShared = stop == end
                    && lineIndex + 1 < lines.Count
                    && stop == lines[lineIndex + 1].Start
                    && !isHardBreakBeforeStop;
                var isSoftWrapStart = stop == start
                    && lineIndex > 0
                    && stop == lines[lineIndex - 1].End
                    && !isHardBreakBeforeStop;

                if (isSoftWrapShared)
                {
                    // 软换行上行末：X = 行内容宽（不含行尾空白——stub 全宽模型无 TrimEnd）。
                    carets.Add(new CaretPosition(stop, lineOffsets[lineIndex] + ((contentEnd - start) * glyphWidth), lineIndex));
                }
                else if (isSoftWrapStart)
                {
                    // 软换行下行首：X = OffsetX。
                    carets.Add(new CaretPosition(stop, lineOffsets[lineIndex], lineIndex));
                }
                else if (stop == end && lineIndex + 1 < lines.Count && stop == lines[lineIndex + 1].Start && isHardBreakBeforeStop)
                {
                    // 硬换行后的行首 offset：归属下行（LineStart），上行不产 caret。
                    continue;
                }
                else
                {
                    // 行内/硬换行后行首/terminator 内 stop：X = OffsetX + (min(stop,contentEnd)-start)*glyphWidth，
                    // terminator 不算内容宽度（换行符不是 glyph）。
                    var xStop = Math.Min(stop, contentEnd);
                    carets.Add(new CaretPosition(stop, lineOffsets[lineIndex] + ((xStop - start) * glyphWidth), lineIndex));
                }
            }

            result.Add(carets);
        }

        return result;
    }

    /// <summary>定位含 stop 的行（首个 Start≤stop≤End 的行）；与真实 <c>TextLayoutBuilder</c> 的同名实现一致——
    /// 共享 offset 归上行，legacy caretXs 语义不变；新归属表（caretsByLine）负责下行真实坐标。</summary>
    private static int LineIndexForStop(List<(int Start, int End)> lines, int stop)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (stop >= lines[i].Start && stop <= lines[i].End)
            {
                return i;
            }
        }

        return lines.Count - 1;
    }

    private static List<(int Start, int End)> WrapClusters(string text, List<int> clusters, double glyphWidth, double maxWidth)
    {
        var lines = new List<(int Start, int End)>();
        if (text.Length == 0)
        {
            lines.Add((0, 0));
            return lines;
        }

        var perLine = maxWidth.Equals(double.PositiveInfinity)
            ? int.MaxValue
            : Math.Max(1, (int)Math.Floor(maxWidth / glyphWidth));
        var lineStart = 0;
        var count = 0;
        for (var i = 0; i < clusters.Count; i++)
        {
            count++;
            if (count >= perLine)
            {
                var end = i + 1 < clusters.Count ? clusters[i + 1] : text.Length;
                lines.Add((lineStart, end));
                lineStart = end;
                count = 0;
            }
        }

        if (lineStart < text.Length || lines.Count == 0)
        {
            lines.Add((lineStart, text.Length));
        }

        return lines;
    }
    private static List<int> GraphemeStarts(string text)
    {
        var result = new List<int>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.ElementIndex);
        }

        return result;
    }

    // IRichTextService：等宽估算镜像真实契约（segment 宽 = 段内字素数 × 字号 × CharWidthScale；
    // 行高 = 行内最大字号 × 1.25 或显式 LineHeight 钳制到行内字高下限；hit/rangerects 可用）。
    // core 无原生资源，backend 是计数 no-op 占位。
    // ------------------------------------------------------------------

    private readonly LruCache<RichTextLayoutRequest, RichLayoutCore> _richLayouts = CreateRichCache();
    private readonly LruCache<RichStubRasterKey, PixelBuffer> _richRasters = new(32);
    private int _richLayoutMisses;
    private int _richRasterMisses;

    /// <summary>富文本实际排版次数。</summary>
    public int RichLayoutMisses => _richLayoutMisses;

    /// <summary>富文本实际栅格化次数。</summary>
    public int RichRasterMisses => _richRasterMisses;

    /// <inheritdoc />
    int IRichTextService.CachedLayoutCount => _richLayouts.Count;

    /// <inheritdoc />
    int IRichTextService.CachedRasterCount => _richRasters.Count;

    /// <inheritdoc />
    int IRichTextService.LayoutCacheCapacity
    {
        get => _richLayouts.Capacity;
        set => _richLayouts.Capacity = value;
    }

    private static LruCache<RichTextLayoutRequest, RichLayoutCore> CreateRichCache()
    {
        var cache = new LruCache<RichTextLayoutRequest, RichLayoutCore>(64);
        cache.OnEvicted = (_, core) => core.Release();
        return cache;
    }

    /// <inheritdoc />
    public RichTextLayoutResult Layout(RichTextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_richLayouts.TryGet(request, out var cached))
        {
            cached.AddRef();
            return new RichTextLayoutResult(cached);
        }

        _richLayoutMisses++;
        var core = BuildRich(request);
        core.AddRef();
        _richLayouts.GetOrAdd(request, _ => core, out _);
        return new RichTextLayoutResult(core);
    }

    /// <inheritdoc />
    public Size Measure(RichTextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = Layout(request);
        try
        {
            return handle.Size;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 替身输出真实可用的 RGBA 位图：按 run 前景色把各 segment 的行内矩形涂成该颜色
    /// （预乘语义——alpha 通道直接写 color.A，像素 RGB 按 alpha 预乘），保证调用方能区分
    /// 不同 paint。背景/装饰字段进缓存键但不烘入位图（装饰由视图层绘制，与真实实现一致）。
    /// </remarks>
    public PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(paint);
        var identity = layout.LayoutIdentity;
        if (paint.Runs.Count != layout.Segments.Count)
        {
            throw new ArgumentException(
                $"paint.Runs 数量（{paint.Runs.Count}）必须与 layout.Segments（{layout.Segments.Count}）一一对应。",
                nameof(paint));
        }

        var key = new RichStubRasterKey(identity, paint);
        if (_richRasters.TryGet(key, out var cached))
        {
            return cached;
        }

        _richRasterMisses++;
        var scale = layout.PixelScale <= 0 ? 1 : layout.PixelScale;
        // buffer 覆盖 RasterBounds 的物理取整范围；stub 该值即布局盒，行为与真实实现同源。
        var bounds = layout.RasterBounds;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Right * scale) - (int)Math.Floor(bounds.Left * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Bottom * scale) - (int)Math.Floor(bounds.Top * scale));
        var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
        var originX = Math.Floor(bounds.Left * scale) / scale;
        var originY = Math.Floor(bounds.Top * scale) / scale;
        var segmentIndex = 0;
        foreach (var segment in layout.Segments)
        {
            var color = paint.Runs[segmentIndex].Foreground;
            segmentIndex++;
            var line = layout.Lines[segment.LineIndex];
            var left = (int)Math.Round((segment.X - originX) * scale);
            var top = (int)Math.Round((line.Bounds.Y - originY) * scale);
            var right = (int)Math.Round((segment.X + segment.Width - originX) * scale);
            var bottom = (int)Math.Round((line.Bounds.Bottom - originY) * scale);
            var r = (byte)((color.R * color.A + 127) / 255);
            var g = (byte)((color.G * color.A + 127) / 255);
            var b = (byte)((color.B * color.A + 127) / 255);
            for (var y = Math.Max(0, top); y < Math.Min(height, bottom); y++)
            {
                for (var x = Math.Max(0, left); x < Math.Min(width, right); x++)
                {
                    var offset = (y * buffer.Stride) + (x * 4);
                    buffer.Pixels[offset] = r;
                    buffer.Pixels[offset + 1] = g;
                    buffer.Pixels[offset + 2] = b;
                    buffer.Pixels[offset + 3] = color.A;
                }
            }
        }

        _richRasters.GetOrAdd(key, _ => buffer, out _);
        return buffer;
    }

    /// <inheritdoc />
    void IRichTextService.ClearCaches()
    {
        _richLayouts.Clear();
        _richRasters.Clear();
    }

    private readonly record struct RichStubRasterKey(RichLayoutIdentity Identity, RichTextPaint Paint);

    /// <summary>替身后端：无原生资源，Release/Dispose 仅计数 no-op（语义与真实 core 一致便于测试）。</summary>
    private sealed class StubRichBackend : IRichTextLayoutBackend
    {
        public void Dispose()
        {
        }
    }

    private RichLayoutCore BuildRich(RichTextLayoutRequest request)
    {
        var styled = request.Text;
        var text = styled.Text;
        var clusters = GraphemeStarts(text);
        if (clusters.Count == 0 || clusters[^1] != text.Length)
        {
            clusters.Add(text.Length);
        }
        // 每字素的模型段序号：纯查找（不捕获游标；行 metrics 与 run 聚合两轮都从头按位置定位）。
        int SegmentAt(int clusterStart)
        {
            var seg = 0;
            while (seg < styled.Segments.Count && styled.Segments[seg].End <= clusterStart)
            {
                seg++;
            }

            return seg;
        }

        double WidthOfSegment(int modelSegment)
        {
            var size = styled.Segments[modelSegment].FontSignature.Size ?? request.ParagraphFont.Size;
            return Math.Max(1, size * CharWidthScale);
        }

        // 断行：wrap 时按累计宽贪心切；硬换行切段落（\r\n 计一个 terminator）。
        var lines = new List<(int Start, int End, int TrimEnd)>();
        var lineStart = 0;
        var lineWidth = 0.0;
        var lineFirst = 0;
        var spacing = request.LetterSpacing;
        for (var i = 0; i + 1 < clusters.Count; i++)
        {
            var start = clusters[i];
            if (IsLineBreak(text[start]))
            {
                var end = start + 1;
                if (text[start] == '\r' && end < text.Length && text[end] == '\n')
                {
                    end++;
                }

                lines.Add((lineStart, end, start));
                lineStart = end;
                lineWidth = 0;
                lineFirst = i + 1;
                continue;
            }

            var w = WidthOfSegment(SegmentAt(start));
            if (request.Wrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(request.MaxWidth)
                && i > lineFirst && lineWidth + w > request.MaxWidth)
            {
                lines.Add((lineStart, start, start));
                lineStart = start;
                lineWidth = 0;
                lineFirst = i;
            }

            lineWidth += w;
        }

        if (lineStart < text.Length || lines.Count == 0)
        {
            lines.Add((lineStart, text.Length, text.Length));
        }

        // 行/segment/caret 生成。
        var lineInfos = new List<TextLineInfo>(lines.Count);
        var segments = new List<RichTextSegment>();
        var caretsByLine = new LineCarets[lines.Count];
        var caretXs = new double[clusters.Count];
        var fonts = new List<ResolvedFontIdentity>();
        var fontIds = new Dictionary<FontDescription, int>();
        var top = 0.0;

        for (var li = 0; li < lines.Count; li++)
        {
            var (ls, le, ltrim) = lines[li];

            // 行 metrics：行内最大字号 × 1.25（或显式 LineHeight 钳到字高下限）。
            var maxSize = request.ParagraphFont.Size;
            for (var i = 0; i + 1 < clusters.Count; i++)
            {
                if (clusters[i] >= ls && clusters[i] < ltrim)
                {
                    maxSize = Math.Max(maxSize, styled.Segments[SegmentAt(clusters[i])].FontSignature.Size ?? request.ParagraphFont.Size);
                }
            }

            var fontHeight = maxSize * 1.25;
            var lineHeight = request.LineHeight > 0 ? Math.Max(request.LineHeight, fontHeight) : fontHeight;
            var baseline = top + ((lineHeight - fontHeight) / 2) + (fontHeight * 0.8);

            // 行内 segment：同模型段的连续簇聚合；X 从行起点累计（含字距）。
            var offsetX = 0.0;
            var stops = new List<int>();
            var xs = new List<double>();
            var x = offsetX;
            var runStart = -1;
            var runSeg = -1;
            var runX = 0.0;
            var runW = 0.0;
            var firstInLine = true;

            void FlushRun(int boundary)
            {
                if (runStart < 0)
                {
                    return;
                }

                var modelSeg = styled.Segments[runSeg];
                var font = new FontDescription(
                    modelSeg.FontSignature.Family ?? request.ParagraphFont.Family,
                    modelSeg.FontSignature.Size ?? request.ParagraphFont.Size,
                    modelSeg.FontSignature.Weight ?? request.ParagraphFont.Weight,
                    modelSeg.FontSignature.Italic ?? request.ParagraphFont.Italic);
                if (!fontIds.TryGetValue(font, out var fontId))
                {
                    fontId = fonts.Count;
                    fonts.Add(ResolvedFontIdentity.Create(font, font.Family.DisplayName, ResolvedFontIdentity.NextFaceId()));
                    fontIds[font] = fontId;
                }

                segments.Add(new RichTextSegment(li, runStart, boundary, fontId, runX, runW, baseline));
                runStart = -1;
            }

            for (var i = 0; i + 1 < clusters.Count; i++)
            {
                var cs = clusters[i];
                if (cs < ls)
                {
                    continue;
                }

                if (cs >= ltrim)
                {
                    break;
                }

                // caret stop：行首（软换行共享/硬换行后）取 offsetX；行内按累计 x。
                if (firstInLine)
                {
                    stops.Add(cs);
                    xs.Add(offsetX);
                    firstInLine = false;
                }

                var seg = SegmentAt(cs);
                if (runSeg != seg)
                {
                    FlushRun(cs);
                    runStart = cs;
                    runSeg = seg;
                    runX = x;
                    runW = 0;
                }

                var w = WidthOfSegment(seg);
                runW += w;
                x += w + spacing;

                // 每个字素起点之后写一个 caret stop（下一边界的 X）。
                var next = clusters[i + 1];
                stops.Add(next);
                xs.Add(x);
            }

            FlushRun(ltrim);

            // 软换行共享 offset / 行尾：行末 stop 若已在字素循环写过（next==le 且下一行 Start==le），
            // 上面的 stops 已含 le（X=行右缘）；下行首在下一行 firstInLine 分支写 offsetX。
            var contentWidth = x;
            lineInfos.Add(new TextLineInfo(ls, le, new Rect(offsetX, top, contentWidth, lineHeight), baseline));
            caretsByLine[li] = new LineCarets(stops, xs);

            // 全局 caretXs：吸附到 stop（行内单调，跨行共享 offset 归上行值）。
            for (var i = 0; i < clusters.Count; i++)
            {
                var stop = clusters[i];
                if (stop < ls || stop > le)
                {
                    continue;
                }

                var idx = stops.IndexOf(stop);
                if (idx >= 0)
                {
                    caretXs[i] = xs[idx];
                }
            }

            top += lineHeight;
        }

        var maxLineWidth = 0.0;
        foreach (var info in lineInfos)
        {
            maxLineWidth = Math.Max(maxLineWidth, info.Bounds.Width);
        }

        return new RichLayoutCore(
            text,
            request.ParagraphFont,
            request.PixelScale,
            request.LetterSpacing,
            new Size(maxLineWidth, top),
            lineInfos,
            segments,
            fonts,
            clusters,
            caretXs,
            caretsByLine,
            // 等宽估算无真实墨迹：RasterBounds = 布局盒。
            Rect.FromLtrb(0, 0, maxLineWidth, top),
            new StubRichBackend());
    }
}

/// <summary>
/// 测试用纹理服务：不上传 GPU 资源，只分配递增句柄并记录创建/释放数量。
/// </summary>
public sealed class RecordingTextureService : ITextureService
{
    // 键按 Equals 比较：值类型键（例如文本纹理键）按键值，引用类型键（例如图片资源）按引用。
    private readonly Dictionary<object, TextureHandle> _map = new(EqualityComparer<object>.Default);
    private readonly Queue<object> _order = new();

    // 帧保护分两代（本帧 / 上一帧），与 GlTextureService 的语义一致：避免同帧内互相挤掉导致重复上传。
    private HashSet<object> _frameReferenced = new(EqualityComparer<object>.Default);
    private HashSet<object> _previousFrameReferenced = new(EqualityComparer<object>.Default);
    private long _nextId;
    private int _created;
    private int _released;

    /// <inheritdoc />
    public int LiveTextureCount => _map.Count;

    /// <summary>累计创建的纹理数量。</summary>
    public int CreatedCount => _created;

    /// <summary>累计释放的纹理数量。</summary>
    public int ReleasedCount => _released;

    /// <summary>工厂被调用的次数（即实际上传次数）。</summary>
    public int FactoryInvocations { get; private set; }

    /// <summary>最近一次上传的位图尺寸。</summary>
    public Size? LastUploadSize { get; private set; }

    private int _capacity = 256;

    /// <inheritdoc />
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(1, value);
            Trim();
        }
    }

    /// <inheritdoc />
    public TextureHandle GetOrCreate(object key, Func<PixelBuffer> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        if (_map.TryGetValue(key, out var existing))
        {
            _frameReferenced.Add(key);
            return existing;
        }

        var buffer = factory();
        FactoryInvocations++;
        LastUploadSize = new Size(buffer.Width, buffer.Height);
        var handle = new TextureHandle(
            Interlocked.Increment(ref _nextId),
            buffer.Width,
            buffer.Height,
            buffer.Format == PixelFormat.Alpha8);
        _created++;
        _map[key] = handle;
        _order.Enqueue(key);
        _frameReferenced.Add(key);
        Trim();
        return handle;
    }

    /// <inheritdoc />
    /// <remarks>清除上一帧的引用保护并尝试回落容量；随后本帧引用的纹理存活到下一次调用。</remarks>
    public void BeginFrame()
    {
        _previousFrameReferenced = _frameReferenced;
        _frameReferenced = new HashSet<object>(EqualityComparer<object>.Default);
        Trim();
    }

    private bool IsProtected(object key) => _frameReferenced.Contains(key) || _previousFrameReferenced.Contains(key);

    private void Trim()
    {
        // 只淘汰未被保护的键：容量是长期上限，帧内引用可临时超出，在后续帧边界回收。
        var pending = new Queue<object>();
        while (_map.Count > Capacity && _order.Count > 0)
        {
            var oldest = _order.Dequeue();
            if (IsProtected(oldest))
            {
                pending.Enqueue(oldest);
                continue;
            }

            if (_map.Remove(oldest))
            {
                _released++;
            }
        }

        while (pending.Count > 0)
        {
            _order.Enqueue(pending.Dequeue());
        }
    }

    /// <inheritdoc />
    public void Release(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _frameReferenced.Remove(key);
        _previousFrameReferenced.Remove(key);
        if (_map.Remove(key))
        {
            _released++;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _released += _map.Count;
        _map.Clear();
        _order.Clear();
    }
}

/// <summary>测试用图片服务：按注册的来源返回位图，未注册的来源抛出 <see cref="ImageLoadException"/>。</summary>
public sealed class StubImageService : IImageService
{
    private readonly Dictionary<string, ImageResource> _resources = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <inheritdoc />
    public int CachedImageCount => _resources.Count;

    private int _capacity = 16;

    /// <inheritdoc />
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(1, value);
            while (_resources.Count > _capacity && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                if (_resources.Remove(oldest, out var evicted))
                {
                    evicted.Dispose();
                }
            }
        }
    }

    /// <summary>加载失败的次数（用于验证诊断）。</summary>
    public int FailureCount { get; private set; }

    /// <summary>注册（或替换）一个来源对应的图片。</summary>
    public ImageResource Register(string source, int width, int height, double scale = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
        Array.Fill(buffer.Pixels, (byte)255);
        var resource = new ImageResource(source, buffer, scale);
        if (_resources.TryGetValue(source, out var existing))
        {
            existing.Dispose();
        }
        else
        {
            _order.Enqueue(source);
        }

        _resources[source] = resource;
        while (_resources.Count > Capacity && _order.Count > 0)
        {
            var oldest = _order.Dequeue();
            if (oldest != source && _resources.Remove(oldest, out var evicted))
            {
                evicted.Dispose();
            }
        }

        return resource;
    }

    /// <inheritdoc />
    public ImageResource Load(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (_resources.TryGetValue(source, out var resource) && !resource.IsDisposed)
        {
            return resource;
        }

        FailureCount++;
        throw new ImageLoadException(source, "测试替身未注册该来源");
    }

    /// <inheritdoc />
    public bool TryLoad(string source, out ImageResource? resource)
    {
        if (_resources.TryGetValue(source, out var found) && !found.IsDisposed)
        {
            resource = found;
            return true;
        }

        FailureCount++;
        resource = null;
        return false;
    }

    /// <inheritdoc />
    public void Clear()
    {
        foreach (var resource in _resources.Values)
        {
            resource.Dispose();
        }

        _resources.Clear();
        _order.Clear();
    }
}

/// <summary>测试用剪贴板服务。</summary>
public sealed class StubClipboardService : IClipboardService
{
    private string _text = string.Empty;

    /// <summary>写入次数。</summary>
    public int SetCount { get; private set; }

    /// <inheritdoc />
    public string? GetText() => _text;

    /// <inheritdoc />
    public void SetText(string text)
    {
        _text = text ?? string.Empty;
        SetCount++;
    }
}

/// <summary>测试用滚轮设置服务：返回可替换的固定设置，不读取任何系统或平台 API。</summary>
public sealed class StubWheelScrollSettingsService : IWheelScrollSettingsService
{
    /// <summary>由设置值创建。</summary>
    public StubWheelScrollSettingsService(WheelScrollSettings settings) => Settings = settings;

    /// <inheritdoc />
    public WheelScrollSettings Settings { get; set; }
}
