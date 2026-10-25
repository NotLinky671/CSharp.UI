using System.Collections.ObjectModel;

namespace CSharp.UI;

/// <summary>
/// 一个真实解析出的渲染 face 的后端无关身份：请求的 <see cref="FontDescription"/> +
/// 实际命中的字体族名 + 稳定进程内 face 标识。<see cref="RichTextSegment.FontId"/> 索引到
/// <see cref="RichTextLayoutResult.ResolvedFonts"/> 的一项——回退 face 与主 face 身份不同，
/// 即使显示名相同也绝不合并（<see cref="FaceId"/> 唯一）。
/// </summary>
public sealed record ResolvedFontIdentity
{
    private static int _nextFaceId;

    /// <summary>
    /// 创建一条 resolved face 身份（排版方调用；每个真实解析出的 face 一次）。
    /// <paramref name="requested"/> 是模型/段落请求的字体描述（含冻结 family fallbacks 时原样传入即可）；
    /// <paramref name="faceId"/> 用 <see cref="NextFaceId"/> 分配的进程内唯一值。
    /// </summary>
    public static ResolvedFontIdentity Create(FontDescription requested, string resolvedFamilyName, int faceId)
        => new() { Requested = requested, ResolvedFamilyName = resolvedFamilyName, FaceId = faceId };

    /// <summary>分配一个新的进程内稳定 face 标识（每个真实解析出的 face 一次；非跨进程持久 ID）。</summary>
    public static int NextFaceId() => Interlocked.Increment(ref _nextFaceId);

    private ResolvedFontIdentity()
    {
    }

    /// <summary>该 face 对应的请求字体（段落默认或模型段 resolved 结果）。</summary>
    public required FontDescription Requested { get; init; }

    /// <summary>实际解析命中的字体族显示名（回退时为回退字体名，非请求名）。</summary>
    public required string ResolvedFamilyName { get; init; }

    /// <summary>
    /// 进程内稳定 face 标识：同一真实 face（同底层字体对象，按引用）唯一；
    /// 不同 face 即使 <see cref="ResolvedFamilyName"/> 相同也不同——不是跨进程持久 ID。
    /// </summary>
    public required int FaceId { get; init; }

    /// <summary>实际字号（逻辑像素，等于 <see cref="Requested"/> 的字号）。</summary>
    public double Size => Requested.Size;

    /// <summary>实际笔画粗细（等于 <see cref="Requested"/> 的粗细）。</summary>
    public FontWeight Weight => Requested.Weight;

    /// <summary>是否斜体。</summary>
    public bool Italic => Requested.Italic;
}

/// <summary>
/// 一个排版后的几何片段（canonical segment）：不跨任何 <see cref="StyledText"/> 模型端点的展示区间；
/// 可能在模型端点内因换行、字体回退 face、script 差异再细分。坐标相对文本块左上角，逻辑像素。
/// </summary>
/// <param name="LineIndex">所属视觉行（<see cref="RichTextLayoutResult.Lines"/> 下标）。</param>
/// <param name="Start">区间起始 UTF-16 偏移（含）。</param>
/// <param name="End">区间结束 UTF-16 偏移（不含）。</param>
/// <param name="FontId">该段的 resolved 字体身份；同 <see cref="RichTextLayoutResult.ResolvedFonts"/> 下标。</param>
/// <param name="X">段在行内的水平起点（已含对齐偏移）。</param>
/// <param name="Width">段宽（advance 之和）。</param>
/// <param name="Baseline">该段绘制基线 Y（不同字号段各自有基线，行盒内对齐到行基线）。</param>
public readonly record struct RichTextSegment(int LineIndex, int Start, int End, int FontId, double X, double Width, double Baseline);

/// <summary>
/// 一次富文本排版请求（GeometryKey）：几何相关字段全值相等即排版缓存命中；
/// 颜色/payload 的值不参与。<see cref="StyledText"/> 经预计算拓扑与每段几何样式签名进入键。
/// </summary>
public sealed record RichTextLayoutRequest
{
    /// <summary>展示文本（含样式/交互区间与分段拓扑）。</summary>
    public required StyledText Text { get; init; }
    /// <summary>
    /// 段落默认字体；段的 <see cref="TextStyle"/> 字体字段为 null 时继承它。
    /// 赋值时若 <see cref="FontFamily.Fallbacks"/> 非空则冻结为独立只读快照（与 RT-01 同款隔离：
    /// 调用方事后修改底层可变列表不会改变缓存键的相等/哈希语义）。
    /// </summary>
    public FontDescription ParagraphFont
    {
        get => _paragraphFont;
        init => _paragraphFont = FreezeRequestFont(value);
    }

    private readonly FontDescription _paragraphFont = FontDescription.Default;

    /// <summary>可用宽度上限（逻辑像素）；<see cref="double.PositiveInfinity"/> 表示不换行。</summary>
    public double MaxWidth { get; init; } = double.PositiveInfinity;

    /// <summary>换行方式；富文本默认 <see cref="TextWrapping.Wrap"/>（多行展示为主场景）。</summary>
    public TextWrapping Wrapping { get; init; } = TextWrapping.Wrap;

    /// <summary>水平对齐。</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>行高（逻辑像素）；0 表示按行内最大字高。显式值小于行内字高时按字高钳制（glyph 不越行盒）。</summary>
    public double LineHeight { get; init; }

    /// <summary>DPI 缩放（逻辑像素 → 物理像素）。参与缓存键。</summary>
    public double PixelScale { get; init; } = 1.0;

    /// <summary>字距调整（逻辑像素）：每个字素簇之后附加的水平推进量。</summary>
    public double LetterSpacing { get; init; }

    /// <summary>
    /// GeometryKey 相等：文本本体 + 分段拓扑/每段几何签名（不含颜色/payload/Id 值）+ 段落字体 + 排版参数。
    /// 颜色值或 payload 值不同（端点不变）的两请求相等；端点或字体签名不同则不等。
    /// </summary>
    public bool Equals(RichTextLayoutRequest? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Text.Text, other.Text.Text, StringComparison.Ordinal)
            && Text.TopologyEquals(other.Text)
            && ParagraphFont.Equals(other.ParagraphFont)
            && MaxWidth.Equals(other.MaxWidth)
            && Wrapping == other.Wrapping
            && Alignment == other.Alignment
            && LineHeight.Equals(other.LineHeight)
            && PixelScale.Equals(other.PixelScale)
            && LetterSpacing.Equals(other.LetterSpacing);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Text.Text, StringComparer.Ordinal);
        hash.Add(Text.TopologyHashCode);
        hash.Add(ParagraphFont);
        hash.Add(MaxWidth);
        hash.Add(Wrapping);
        hash.Add(Alignment);
        hash.Add(LineHeight);
        hash.Add(PixelScale);
        hash.Add(LetterSpacing);
        return hash.ToHashCode();
    }

    /// <summary>冻结 <see cref="FontDescription.Family"/> 的 fallbacks；无 fallback 时原样返回（零新分配）。</summary>
    private static FontDescription FreezeRequestFont(FontDescription font)
        => font.Family.Fallbacks.Count > 0
            ? font with { Family = font.Family.FrozenSnapshot() }
            : font;
}

/// <summary>
/// 一个 canonical segment 的绘制属性：已解析（不含继承位）的前/背景色与装饰标志。
/// 前景色 <see cref="Color.A"/> 参与预乘输出；装饰字段本轮只作为 paint 键与元数据，
/// 装饰像素由 RT-03 视图以矩形命令绘制（栅格层不烘背景/装饰，避免双层绘制）。
/// </summary>
public readonly record struct RichTextRunPaint(Color Foreground, Color Background, bool Underline, bool Strikethrough);

/// <summary>
/// 一次栅格化使用的完整 paint：与 <see cref="RichTextLayoutResult.Segments"/> 一一对应的
/// <see cref="Runs"/> + 段落默认前景。值相等；构造时冻结为独立只读快照并验证数量。
/// </summary>
public sealed class RichTextPaint : IEquatable<RichTextPaint>
{
    private readonly int _hashCode;

    /// <summary>
    /// 构造 paint。<paramref name="runs"/> 复制为内部快照，调用方事后修改集合不影响本实例。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="runs"/> 为空。</exception>
    public RichTextPaint(IReadOnlyList<RichTextRunPaint> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("paint 至少需要一个 run。", nameof(runs));
        }

        var copy = new RichTextRunPaint[runs.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = runs[i];
        }

        Runs = new ReadOnlyCollection<RichTextRunPaint>(copy);
        var hash = new HashCode();
        hash.Add(ParagraphForeground);
        foreach (var run in copy)
        {
            hash.Add(run);
        }

        _hashCode = hash.ToHashCode();
    }

    /// <summary>每段已解析绘制属性，与 <see cref="RichTextLayoutResult.Segments"/> 一一对应（同序号、同序）。</summary>
    public IReadOnlyList<RichTextRunPaint> Runs { get; }

    /// <summary>段落默认前景（run 前景全由调用方按此解算；本字段进 paint 键，供绘制端区分语义来源）。</summary>
    public Color ParagraphForeground { get; init; } = Color.Black;

    /// <inheritdoc />
    public bool Equals(RichTextPaint? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (ParagraphForeground != other.ParagraphForeground || Runs.Count != other.Runs.Count)
        {
            return false;
        }

        for (var i = 0; i < Runs.Count; i++)
        {
            if (Runs[i] != other.Runs[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RichTextPaint);

    /// <inheritdoc />
    public override int GetHashCode() => _hashCode;
}

/// <summary>
/// 排版结果共享体的后端无关稳定身份：同一 <see cref="RichLayoutCore"/> 的全部 handle 共享；
/// 不同 core（即使几何内容相同）身份不同。只用于栅格/纹理缓存键比较，不是排版缓存键。
/// </summary>
public readonly record struct RichLayoutIdentity(int Value);

/// <summary>
/// 富文本排版结果的调用方句柄：公开几何面（尺寸/行/segment/caret/命中），
/// <see cref="IDisposable.Dispose"/> 释放对共享 <see cref="RichLayoutCore"/> 的一份引用（幂等）。
/// 已 Dispose 的句柄调用任何 API 抛 <see cref="ObjectDisposedException"/>。
/// </summary>
/// <remarks>
/// 不持 <see cref="StyledText"/>/payload 引用；paint 由调用方按当前样式区间与
/// <see cref="Segments"/> 的文本区间归并后经 <see cref="IRichTextService.Rasterize"/> 传入。
/// </remarks>
public sealed class RichTextLayoutResult : IDisposable
{
    private RichLayoutCore? _core;
    /// <summary>由服务构造调用方句柄；每份句柄对 core 持一份引用。</summary>
    internal RichTextLayoutResult(RichLayoutCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    /// <summary>
    /// 公开构造：供**外部 <see cref="IRichTextService"/> 实现**以自包含几何数据构建调用方句柄
    /// （与 <see cref="TextLayoutResult"/> 的公开构造同款；不暴露内部 core/后端类型）。
    /// 所有列表复制为只读快照；<paramref name="owner"/> 可选——提供时在最后一个句柄
    /// <see cref="Dispose"/> 时 Dispose 一次，用于实现方归还自有资源（默认无资源）。
    /// </summary>
    /// <param name="caretsByLine">每行 caret 子表（与 <paramref name="lines"/> 一一对应；
    /// 软换行共享 offset 在两行各有独立 X）。</param>
    /// <param name="rasterBounds">墨迹包围盒（逻辑像素，相对排版原点；可为实现方的实际
    /// 墨迹 union ∪ 布局盒，或纯几何实现直接用布局盒）。</param>
    public RichTextLayoutResult(
        string text,
        FontDescription paragraphFont,
        double pixelScale,
        double letterSpacing,
        Size size,
        IReadOnlyList<TextLineInfo> lines,
        IReadOnlyList<RichTextSegment> segments,
        IReadOnlyList<ResolvedFontIdentity> resolvedFonts,
        IReadOnlyList<int> caretStops,
        IReadOnlyList<double> caretXs,
        IReadOnlyList<LineCarets> caretsByLine,
        Rect rasterBounds,
        IDisposable? owner = null)
        : this(new RichLayoutCore(
            text, paragraphFont, pixelScale, letterSpacing, size,
            lines, segments, resolvedFonts, caretStops, caretXs, caretsByLine,
            rasterBounds, owner is null ? NoopRichBackend.Instance : new OwnerBackend(owner)))
    {
    }

    /// <summary>
    /// 在已活句柄上再取一份共享同一 core 的独立句柄（供实现方的缓存持有份额）；
    /// 与 <see cref="IRichTextService.Layout"/> 返回的句柄同样独立 Dispose。
    /// </summary>
    public RichTextLayoutResult RetainHandle()
    {
        var core = Core;
        core.AddRef();
        return new RichTextLayoutResult(core);
    }

    private RichLayoutCore Core => _core ?? throw new ObjectDisposedException(nameof(RichTextLayoutResult));

    /// <summary>共享 core 的稳定身份；栅格缓存键的一部分。同一 core 全部句柄相同。</summary>
    public RichLayoutIdentity LayoutIdentity => Core.Identity;

    /// <summary>展示文本。</summary>
    public string Text => Core.Text;

    /// <summary>段落默认字体。</summary>
    public FontDescription ParagraphFont => Core.ParagraphFont;

    /// <summary>排版使用的 DPI 缩放。</summary>
    public double PixelScale => Core.PixelScale;

    /// <summary>文本块尺寸（逻辑像素）。</summary>
    public Size Size => Core.Size;

    /// <summary>字距（逻辑像素）。</summary>
    public double LetterSpacing => Core.LetterSpacing;

    /// <summary>
    /// 墨迹包围盒（逻辑像素，相对排版原点）：glyph 真实 ink 边界 ∪ 文本块盒，
    /// 可越出 <see cref="Size"/>（如斜体尾画/j 负 bearing）。布局度量不变；
    /// 栅格化 buffer 覆盖本矩形在 <see cref="PixelScale"/> 下向外取整的物理范围。
    /// </summary>
    public Rect RasterBounds => Core.RasterBounds;

    /// <summary>
    /// 栅格化位图像素 (0,0) 对应的逻辑坐标：= floor(RasterBounds.Left·scale)/scale、
    /// floor(RasterBounds.Top·scale)/scale。视图把纹理放在排版原点 + 本偏移即可
    /// 让像素与 caret/segment 逻辑坐标逐像素对齐。
    /// </summary>
    public Point RasterOrigin
    {
        get
        {
            var scale = PixelScale <= 0 ? 1.0 : PixelScale;
            var bounds = Core.RasterBounds;
            return new Point(Math.Floor(bounds.Left * scale) / scale, Math.Floor(bounds.Top * scale) / scale);
        }
    }

    /// <summary>行集合（与 <see cref="TextLayoutResult.Lines"/> 同语义：Bounds/Baseline 相对文本块左上角）。</summary>
    public IReadOnlyList<TextLineInfo> Lines => Core.Lines;

    /// <summary>canonical geometry segment（按行与行内起点排序，不跨模型端点）。</summary>
    public IReadOnlyList<RichTextSegment> Segments => Core.Segments;

    /// <summary>各 segment 引用的 resolved 字体身份表（<see cref="RichTextSegment.FontId"/> 的下标）。</summary>
    public IReadOnlyList<ResolvedFontIdentity> ResolvedFonts => Core.ResolvedFonts;

    /// <summary>字素边界上的 UTF-16 索引（升序，含 0 与 Text.Length）。</summary>
    public IReadOnlyList<int> CaretStops => Core.CaretStops;

    /// <summary>与 <see cref="CaretStops"/> 对应的 caret X（已含对齐偏移）。</summary>
    public IReadOnlyList<double> CaretXs => Core.CaretXs;

    /// <summary>
    /// 命中测试（显示路径）：先按 Y 定行，再在该行**行内** caret 表比 X 取最近字素边界。
    /// 软换行共享 offset 在两行各有真实 X（行尾=该行右缘、下行=该行左缘）。
    /// </summary>
    public int HitTestIndex(Point position)
    {
        var core = Core;
        var lineIndex = LineIndexForPosition(position);
        var carets = core.LineCarets(lineIndex);
        var best = carets.Stops.Count > 0 ? carets.Stops[0] : 0;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < carets.Stops.Count; i++)
        {
            var distance = Math.Abs(carets.Xs[i] - position.X);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = carets.Stops[i];
            }
        }

        return best;
    }

    /// <summary>共享 core 的后端负载（服务实现层按具体类型消费）；已 Dispose 抛。</summary>
    internal IRichTextLayoutBackend CoreBackend => Core.Backend;

    /// <summary>返回包含指定位置的行的索引；超出范围时取最近的行。</summary>
    public int LineIndexForPosition(Point position)
    {
        var core = Core;
        var lines = core.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (position.Y >= lines[i].Bounds.Top && position.Y < lines[i].Bounds.Bottom)
            {
                return i;
            }
        }

        return position.Y < lines[0].Bounds.Top ? 0 : lines.Count - 1;
    }

    /// <summary>把任意 UTF-16 索引吸附到最近的合法字素边界；<paramref name="roundUp"/> 为 true 时向上取。</summary>
    public int SnapToCaretStop(int index, bool roundUp = false)
    {
        var core = Core;
        var clamped = Math.Clamp(index, 0, core.Text.Length);
        var found = TextLayoutResult.FindStop(core.CaretStops, clamped);
        if (found >= 0)
        {
            return core.CaretStops[found];
        }

        var insertion = ~found;
        if (!roundUp)
        {
            return core.CaretStops[Math.Max(0, insertion - 1)];
        }

        return core.CaretStops[Math.Min(core.CaretStops.Count - 1, insertion)];
    }

    /// <summary>返回指定字素边界索引处的 caret X；索引不在边界上时向下吸附。</summary>
    public double GetCaretX(int index)
    {
        var core = Core;
        var snapped = SnapToCaretStop(index);
        var found = TextLayoutResult.FindStop(core.CaretStops, snapped);
        return core.CaretXs[found >= 0 ? found : Math.Max(0, ~found - 1)];
    }

    /// <summary>
    /// 返回 [start, end) 范围内文本的逐行矩形列表（用于区间装饰/命中）。语义同
    /// <see cref="TextLayoutResult.GetRangeRects"/>：内容区按行内真实 X 截取、行尾空白不产生矩形、
    /// 硬换行 terminator 覆盖时产零宽 marker。
    /// </summary>
    public IReadOnlyList<Rect> GetRangeRects(int start, int end)
    {
        var core = Core;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var snappedStart = SnapToCaretStop(start);
        var snappedEnd = SnapToCaretStop(end, roundUp: true);
        var rects = new List<Rect>();
        if (snappedStart == snappedEnd)
        {
            return rects;
        }

        var text = core.Text;
        for (var lineIndex = 0; lineIndex < core.Lines.Count; lineIndex++)
        {
            var line = core.Lines[lineIndex];
            var lineEndBound = core.LineEndIndex(lineIndex);
            var contentStart = line.Start;
            var contentEnd = lineEndBound;
            var terminatorStart = lineEndBound;
            var terminatorEnd = line.End;

            var selStart = Math.Max(snappedStart, contentStart);
            var selEnd = Math.Min(snappedEnd, contentEnd);
            if (selStart < selEnd)
            {
                var left = core.LocalCaretXInLine(lineIndex, selStart) ?? line.Bounds.X;
                var right = core.LocalCaretXInLine(lineIndex, selEnd) ?? line.Bounds.Right;
                if (right < left)
                {
                    (left, right) = (right, left);
                }

                rects.Add(new Rect(left, line.Bounds.Y, Math.Max(0, right - left), line.Bounds.Height));
            }

            if (terminatorEnd > terminatorStart
                && text[terminatorEnd - 1] is '\n' or '\r'
                && snappedStart < terminatorEnd
                && snappedEnd > terminatorStart)
            {
                var edge = core.LocalCaretXInLine(lineIndex, terminatorStart) ?? line.Bounds.Right;
                rects.Add(new Rect(edge, line.Bounds.Y, 0, line.Bounds.Height));
            }

            if (contentStart == contentEnd && terminatorStart == terminatorEnd
                && line.Start >= snappedStart && line.Start < snappedEnd)
            {
                rects.Add(new Rect(line.Bounds.X, line.Bounds.Y, 0, line.Bounds.Height));
            }
        }

        return rects;
    }

    /// <summary>返回 [start, end) 范围内文本的包围矩形；范围为空时返回零宽矩形。</summary>
    /// <remarks>按 <see cref="GetRangeRects"/> 的实际逐行片段取并集——跨行/trim/CRLF 边界的真实几何，
    /// 不再单独按全局 caret X 推一份可能不一致的包围盒。</remarks>
    public Rect GetRangeBounds(int start, int end)
    {
        var rects = GetRangeRects(start, end);
        if (rects.Count > 0)
        {
            var result = rects[0];
            for (var i = 1; i < rects.Count; i++)
            {
                result = result.Union(rects[i]);
            }

            return result;
        }

        // 空范围：吸附到 caret stop 后返回该位置行高零宽矩形（与 TextLayoutResult 同语义）。
        var core = Core;
        var snapped = SnapToCaretStop(Math.Min(start, end));
        var line = core.GetLineForIndex(snapped);
        var x = core.LocalCaretXInLine(core.LineIndexForStop(snapped), snapped) ?? GetCaretX(snapped);
        return new Rect(x, line.Bounds.Y, 0, line.Bounds.Height);
    }


    /// <summary>归还本句柄持有的 core 引用；幂等。之后任何 API 调用抛 <see cref="ObjectDisposedException"/>。</summary>
    public void Dispose()
    {
        var core = Interlocked.Exchange(ref _core, null);
        core?.Release();
    }
}

/// <summary>
/// 富文本排版结果的共享体（服务缓存 + 全部调用方句柄共享）：几何数据、
/// canonical segment 表、resolved 字体表与后端 payload。内部引用计数；
/// 归零时经 <see cref="IRichTextLayoutBackend"/> 释放后端资源（face 租约等）。
/// </summary>
/// <remarks>
/// 该类型只由 <c>CSharp.UI</c> 内部与 <c>CSharp.UI.Text</c>（经 InternalsVisibleTo）使用；
/// 服务缓存持有 1 份引用，<see cref="IRichTextService.Layout"/> 每次返回的新句柄再持 1 份。
/// </remarks>
internal sealed class RichLayoutCore
{
    private static int _nextIdentity;

    private readonly IRichTextLayoutBackend _backend;
    private readonly LineCarets[] _caretsByLine;
    private int _refCount;

    /// <summary>
    /// 组装一份排版结果；所有列表形参会复制为不可变快照——调用方/构造方传入可写集合不会泄漏到
    /// 已缓存的公开几何上。<paramref name="paragraphFont"/> 的 <see cref="FontFamily.Fallbacks"/>
    /// 同步冻结，与 <see cref="StyledText"/> 的快照语义一致。调用方对 <paramref name="backend"/>
    /// 的所有权随本对象建立而转移。
    /// </summary>
    /// <param name="caretsByLine">
    /// 每行 caret 子表（与 <paramref name="lines"/> 一一对应；行内 stop 升序、X 一一对应）。
    /// 软换行共享 offset 允许同时作为上行末与下行首出现，**各自 X 必须独立**；
    /// 由排版器在行内真实几何上生成，不从全局单 X 列表推导。
    /// </param>
    /// <param name="rasterBounds">墨迹包围盒（逻辑像素，相对排版原点）：glyph ink union ∪ 布局盒；纯几何实现可传布局盒。</param>
    public RichLayoutCore(
        string text,
        FontDescription paragraphFont,
        double pixelScale,
        double letterSpacing,
        Size size,
        IReadOnlyList<TextLineInfo> lines,
        IReadOnlyList<RichTextSegment> segments,
        IReadOnlyList<ResolvedFontIdentity> resolvedFonts,
        IReadOnlyList<int> caretStops,
        IReadOnlyList<double> caretXs,
        IReadOnlyList<LineCarets> caretsByLine,
        Rect rasterBounds,
        IRichTextLayoutBackend backend)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(resolvedFonts);
        ArgumentNullException.ThrowIfNull(caretStops);
        ArgumentNullException.ThrowIfNull(caretXs);
        ArgumentNullException.ThrowIfNull(caretsByLine);
        ArgumentNullException.ThrowIfNull(backend);
        if (caretStops.Count != caretXs.Count)
        {
            throw new ArgumentException("caretStops 与 caretXs 数量必须一致。", nameof(caretXs));
        }

        if (caretStops.Count == 0 || caretStops[0] != 0 || caretStops[^1] != text.Length)
        {
            throw new ArgumentException("caretStops 必须从 0 开始并以 Text.Length 结束。", nameof(caretStops));
        }

        if (caretsByLine.Count != lines.Count)
        {
            throw new ArgumentException("caretsByLine 必须与 lines 一一对应。", nameof(caretsByLine));
        }

        Text = text;
        ParagraphFont = FreezeFont(paragraphFont);
        PixelScale = pixelScale <= 0 ? 1 : pixelScale;
        LetterSpacing = letterSpacing;
        Size = size;
        Lines = Copy(lines);
        Segments = Copy(segments);
        ResolvedFonts = Copy(resolvedFonts);
        CaretStops = Copy(caretStops);
        CaretXs = Copy(caretXs);
        _caretsByLine = Copy(caretsByLine).ToArray();
        RasterBounds = rasterBounds;
        _backend = backend;
        Identity = new RichLayoutIdentity(Interlocked.Increment(ref _nextIdentity));
        _refCount = 1; // 调用方份额（服务再 AddRef 出缓存份额）
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = source[i];
        }

        return new ReadOnlyCollection<T>(copy);
    }


    /// <summary>冻结 <see cref="FontDescription.Family"/> 的 fallbacks 为独立只读快照（RT-01 同款隔离）。</summary>
    private static FontDescription FreezeFont(FontDescription font)
        => font.Family.Fallbacks.Count > 0
            ? font with { Family = font.Family.FrozenSnapshot() }
            : font;

    /// <summary>后端无关稳定身份（自增序号；进程内唯一即满足缓存键用途）。</summary>
    public RichLayoutIdentity Identity { get; }

    /// <summary>展示文本。</summary>
    public string Text { get; }

    /// <summary>段落默认字体（family fallbacks 已冻结）。</summary>
    public FontDescription ParagraphFont { get; }

    /// <summary>DPI 缩放。</summary>
    public double PixelScale { get; }

    /// <summary>字距。</summary>
    public double LetterSpacing { get; }

    /// <summary>文本块尺寸。</summary>
    public Size Size { get; }

    /// <summary>
    /// 墨迹包围盒（逻辑像素，相对排版原点）：全部已排 glyph 的真实 ink 边界与文本块盒的并集。
    /// 斜体/下行字母/j 类负 bearing 可使其越过 <see cref="Size"/> 之外（坐标可为负）。
    /// 逻辑布局（<see cref="Size"/>/Lines/caret/segment）不变，仅栅格化按本矩形取 buffer。
    /// </summary>
    public Rect RasterBounds { get; }

    /// <summary>行集合（只读快照）。</summary>
    public IReadOnlyList<TextLineInfo> Lines { get; }

    /// <summary>canonical segment 表（只读快照）。</summary>
    public IReadOnlyList<RichTextSegment> Segments { get; }

    /// <summary>resolved 字体表（只读快照；family fallbacks 已冻结）。</summary>
    public IReadOnlyList<ResolvedFontIdentity> ResolvedFonts { get; }

    /// <summary>caret stop 表（只读快照）。</summary>
    public IReadOnlyList<int> CaretStops { get; }

    /// <summary>caret X 表（只读快照）。</summary>
    public IReadOnlyList<double> CaretXs { get; }

    /// <summary>后端数据（shaped glyph/租约等）；由服务层在已知实现类型时访问。</summary>
    public IRichTextLayoutBackend Backend => _backend;

    /// <summary>增加一份引用（缓存持有、新调用方句柄）。</summary>
    public void AddRef() => _refCount++;

    /// <summary>归还一份引用；归零时释放后端资源。幂等调用方不得重复调用同一份。</summary>
    public void Release()
    {
        _refCount--;
        if (_refCount <= 0)
        {
            _backend.Dispose();
        }
    }

    /// <summary>指定行的行内 caret 子表（stop 升序 + 行内真实 X；软换行共享 offset 在两行各有独立 X）。</summary>
    public LineCarets LineCarets(int lineIndex) => _caretsByLine[Math.Clamp(lineIndex, 0, _caretsByLine.Length - 1)];

    /// <summary>指定行的行尾 caret stop（行内最后一个可停留 offset）。</summary>
    public int LineEndIndex(int lineIndex)
    {
        var stops = _caretsByLine[Math.Clamp(lineIndex, 0, _caretsByLine.Length - 1)].Stops;
        return stops.Count > 0 ? stops[^1] : Lines[Math.Clamp(lineIndex, 0, Lines.Count - 1)].End;
    }

    /// <summary>指定行内 stop 的真实 X；未命中返回 null。</summary>
    public double? LocalCaretXInLine(int lineIndex, int stop)
    {
        var carets = _caretsByLine[Math.Clamp(lineIndex, 0, _caretsByLine.Length - 1)];
        var found = TextLayoutResult.FindStop(carets.Stops, stop);
        return found >= 0 ? carets.Xs[found] : null;
    }

    /// <summary>按 Lines.Start 二分定位含 stop 的行；stop 在相邻行边界（End==下一行 Start）归上行。</summary>
    public int LineIndexForStop(int stop)
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

    /// <summary>返回指定索引所在行。</summary>
    public TextLineInfo GetLineForIndex(int index)
    {
        foreach (var line in Lines)
        {
            if (index >= line.Start && index <= line.End)
            {
                return line;
            }
        }

        return Lines[^1];
    }
}

/// <summary>一行的 caret 子表（stop 升序与行内真实 X 一一对应）。不可变值型快照；由排版方构造（含外部富文本服务实现）。</summary>
public readonly record struct LineCarets(IReadOnlyList<int> Stops, IReadOnlyList<double> Xs);

/// <summary>
/// 富文本排版结果的后端数据契约：服务实现自定义（shaped glyph 序列、face 租约表等）。
/// <see cref="RichLayoutCore"/> 引用归零时调用 <see cref="IDisposable.Dispose"/> 释放全部后端资源。
/// 栅格化由拥有该后端的服务以具体类型消费，不通过本接口暴露对象。
/// </summary>
internal interface IRichTextLayoutBackend : IDisposable;

/// <summary>无原生资源的后端（纯托管几何 core 的默认 owner）；Dispose 为空操作。</summary>
internal sealed class NoopRichBackend : IRichTextLayoutBackend
{
    public static readonly NoopRichBackend Instance = new();

    private NoopRichBackend()
    {
    }
    public void Dispose()
    {
    }
}

/// <summary>外部实现方传入的资源 owner；core 引用归零时 Dispose 一次。</summary>
internal sealed class OwnerBackend(IDisposable owner) : IRichTextLayoutBackend
{
    public void Dispose() => owner.Dispose();
}

/// <summary>
/// 富文本服务契约：排版、测量与多样式 CPU 栅格化。
/// 与 <see cref="ITextService"/> 正交：<c>CSharp.UI.Skia.SkiaTextService</c> 同实例实现两接口；
/// 外部只实现 <see cref="ITextService"/> 的宿主不会得到本接口。
/// </summary>
/// <remarks>
/// <para>
/// 所有权：服务内部缓存共享 <c>RichLayoutCore</c>（refcount）。<see cref="Layout"/> 每次返回
/// 一个独立的 <see cref="RichTextLayoutResult"/> 句柄（各持 core 一份引用）；缓存淘汰或
/// <see cref="ClearCaches"/> 只归还缓存那份——调用方仍持有的句柄继续合法可用。
/// 句柄 <see cref="IDisposable.Dispose"/> 后调用任何 API 抛 <see cref="ObjectDisposedException"/>。
/// </para>
/// 服务自身 <see cref="IDisposable"/> 释放后：已活句柄的**几何查询**（Size/Lines/Segments/caret/命中）
/// 仍可用（数据自包含，不依赖服务存活）；<see cref="Rasterize"/> 等走服务的调用抛
/// <see cref="ObjectDisposedException"/>——服务不借存活租约暗中继续工作。
/// </para>
/// <para>线程约定：与核心一致，只在 UI 线程使用；实现不需要内部锁。</para>
/// </remarks>
public interface IRichTextService
{
    /// <summary>排版富文本；相同 GeometryKey 命中缓存并返回共享 core 的新句柄（每次调用都返回新句柄对象）。</summary>
    RichTextLayoutResult Layout(RichTextLayoutRequest request);

    /// <summary>测量文本尺寸（逻辑像素），等价于 <see cref="Layout"/> 后读取 Size 并归还句柄。</summary>
    Size Measure(RichTextLayoutRequest request);

    /// <summary>
    /// 按 <paramref name="paint"/> 把已排版结果栅格化为预乘 RGBA 位图
    /// （<see cref="PixelFormat.Rgba8888Premultiplied"/>，尺寸为逻辑尺寸 × <see cref="RichTextLayoutResult.PixelScale"/>）。
    /// 直接使用排版期存储的 glyph 数据绘制，不重新 shape；前景色 alpha 参与预乘（不做强制不透明化）。
    /// </summary>
    /// <param name="layout">调用方持有的排版句柄；已 Dispose 抛 <see cref="ObjectDisposedException"/>。</param>
    /// <param name="paint">
    /// 与 <see cref="RichTextLayoutResult.Segments"/> 一一对应的已解析绘制属性；
    /// 数量不符抛 <see cref="ArgumentException"/>。
    /// </param>
    PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint);

    /// <summary>当前缓存的排版结果数量（共享 core 数）。</summary>
    int CachedLayoutCount { get; }

    /// <summary>当前缓存的栅格化结果数量。</summary>
    int CachedRasterCount { get; }

    /// <summary>排版缓存容量上限；超出后按最近最少使用淘汰（只归还缓存那份引用）。</summary>
    int LayoutCacheCapacity { get; set; }

    /// <summary>清空全部缓存（DPI 或字体变化时调用）；不影响调用方仍持有句柄的合法性。</summary>
    void ClearCaches();
}
