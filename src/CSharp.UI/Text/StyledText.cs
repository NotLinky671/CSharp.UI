using System.Collections.ObjectModel;
using System.Globalization;

namespace CSharp.UI;

/// <summary>一段展示文本上的样式区间。半开 <c>[Start,End)</c>，UTF-16 索引，端点必须落在字素边界上。</summary>
/// <param name="Start">起始 UTF-16 偏移（含）。</param>
/// <param name="End">结束 UTF-16 偏移（不含）。</param>
/// <param name="Style">该区间相对段落默认字体的覆盖样式；null 表示全继承。<b>注意</b>：null 与 <c>new TextStyle()</c>（全 null 字段）在解析语义上等价，但参与值相等时<b>不相等</b>——不做归一化，调用方按需固定一种写法。</param>
public readonly record struct TextStyleRange(int Start, int End, TextStyle? Style);

/// <summary>
/// 一段展示文本上的交互区间（如链接）。半开 <c>[Start,End)</c>，与 <see cref="TextStyleRange"/> 正交，
/// 两表之间允许重叠。
/// </summary>
/// <param name="Start">起始 UTF-16 偏移（含）。</param>
/// <param name="End">结束 UTF-16 偏移（不含）。</param>
/// <param name="Id">
/// 同一逻辑链接的稳定身份：同一链接跨多个样式片段时共用同一 <c>Id</c>；
/// 两个不同链接即使指向同一 URL 也应使用不同 <c>Id</c>。
/// </param>
/// <param name="Payload">
/// 交互数据（如链接目标）。参与值相等与哈希；<b>调用方契约</b>：应提供不可变且
/// <see cref="object.Equals(object?)"/>/<see cref="object.GetHashCode"/> 稳定的值（如值相等 record）。
/// 模型不复制 payload 本身——可变 payload 在构造后被外部修改会破坏相等性，由调用方负责。
/// </param>
public readonly record struct TextInteractionRange(int Start, int End, int Id, object? Payload);

/// <summary>
/// 富文本样式覆盖：每个字段为 <c>null</c> 表示继承段落默认；颜色与装饰字段只影响绘制，
/// 不影响分段拓扑与几何。全值相等，不含 payload。
/// </summary>
public sealed record TextStyle
{
    /// <summary>字体家族；<c>null</c> 继承段落默认。</summary>
    public FontFamily? Family { get; init; }

    /// <summary>字号（绝对逻辑像素）；<c>null</c> 继承段落默认。构造 <see cref="StyledText"/> 时验证为有限正值。</summary>
    public double? Size { get; init; }

    /// <summary>笔画粗细；<c>null</c> 继承段落默认。</summary>
    public FontWeight? Weight { get; init; }

    /// <summary>是否斜体；<c>null</c> 继承段落默认。</summary>
    public bool? Italic { get; init; }

    /// <summary>前景色；<c>null</c> 继承段落前景。</summary>
    public Color? Foreground { get; init; }

    /// <summary>背景色；<c>null</c> 表示无背景。</summary>
    public Color? Background { get; init; }

    /// <summary>背景矩形圆角（逻辑像素）；<c>null</c> 或 <c>0</c> 为直角（向后兼容）。</summary>
    public double? CornerRadius { get; init; }

    /// <summary>前景色按 Emit 时 <see cref="EnvironmentValues.ResolvedTheme"/> 的 accent 解
    /// （<c>true</c> → accent）；<see cref="Foreground"/> 显式值优先于本标记，因此 parse/Build 期不绑主题。</summary>
    public bool? ForegroundIsAccent { get; init; }

    /// <summary>下划线；<c>null</c> 继承段落默认。</summary>
    public bool? Underline { get; init; }

    /// <summary>删除线；<c>null</c> 继承段落默认。</summary>
    public bool? Strikethrough { get; init; }
}

/// <summary>
/// 只影响绘制的样式覆盖（如 <c>RichText.LinkHoverStyle</c>）：不含字体字段，
/// 因此 hover 覆盖永远不会改变分段拓扑或几何，不会产生重排版。
/// </summary>
public sealed record TextPaintStyle
{
    /// <summary>前景色；<c>null</c> 保留原值。</summary>
    public Color? Foreground { get; init; }

    /// <summary>背景色；<c>null</c> 保留原值。</summary>
    public Color? Background { get; init; }

    /// <summary>下划线；<c>null</c> 保留原值。</summary>
    public bool? Underline { get; init; }

    /// <summary>删除线；<c>null</c> 保留原值。</summary>
    public bool? Strikethrough { get; init; }
}

/// <summary>
/// 一段展示文本的几何相关样式字段签名（Family/Size/Weight/Italic）。
/// 颜色、装饰与 payload 不在其中——只含会改变 shape/几何的字段。
/// </summary>
internal readonly record struct StyledTextFontSignature(FontFamily? Family, double? Size, FontWeight? Weight, bool? Italic)
{
    /// <summary>全部字段继承段落默认的签名。</summary>
    public static readonly StyledTextFontSignature Inherit = new(null, null, null, null);
}

/// <summary>
/// 分段拓扑中的一个段：半开区间 <c>[Start,End)</c> 加该段覆盖样式的几何字段签名。
/// 段边界 = <c>{0, Length}</c> ∪ <see cref="StyledText.Ranges"/> 端点 ∪ <see cref="StyledText.Interactions"/> 端点。
/// </summary>
internal readonly record struct StyledTextSegment(int Start, int End, StyledTextFontSignature FontSignature);

/// <summary>
/// 富文本展示模型：单根 UTF-16 展示字符串 + 正交两表——样式区间（<see cref="Ranges"/>，
/// 无 payload）与交互区间（<see cref="Interactions"/>，含稳定 <see cref="TextInteractionRange.Id"/>）。
/// 唯一的文本模型；不持 Markdown 源 offset。
/// </summary>
/// <remarks>
/// <para>
/// 构造时验证（违反抛 <see cref="ArgumentException"/>，不静默 snap/截断）：两表各自按
/// <c>Start</c> 升序、半开、非空、<c>0 ≤ Start</c>、<c>End ≤ Text.Length</c>、表内两两不重叠；
/// 两表之间允许重叠。乱序输入先排序再验证。所有端点必须是 <see cref="StringInfo"/> 字素簇边界
/// （切代理对/组合序列/ZWJ → 抛）。
/// </para>
/// <para>
/// 输入集合复制为内部快照；实例完全不可变，调用方事后修改输入集合或样式对象的
/// <see cref="FontFamily.Fallbacks"/> 列表不影响本实例。
/// </para>
/// <para>
/// 值相等：逐字符 + 逐区间（含 <see cref="TextInteractionRange.Payload"/> 的
/// <see cref="object.Equals(object?)"/>）。构造时预计算全量哈希与分段拓扑；端点集合与每段
/// 几何样式签名对颜色/payload 的值不可见——端点相同仅颜色不同的两实例几何签名相等。
/// </para>
/// </remarks>
public sealed class StyledText : IEquatable<StyledText>
{
    private static readonly StyledText Empty = new(string.Empty, [], []);

    private readonly TextStyleRange[] _ranges;
    private readonly TextInteractionRange[] _interactions;
    private readonly int[] _segmentBoundaries;
    private readonly StyledTextSegment[] _segments;
    private readonly int _hashCode;
    private readonly int _topologyHashCode;

    /// <summary>展示文本（已解码的 UTF-16）。</summary>
    public string Text { get; }

    /// <summary>样式区间快照（按 <c>Start</c> 升序，无 payload）。</summary>
    public IReadOnlyList<TextStyleRange> Ranges { get; }

    /// <summary>交互区间快照（按 <c>Start</c> 升序）。</summary>
    public IReadOnlyList<TextInteractionRange> Interactions { get; }

    /// <summary>是否含任一区间（样式或交互）。</summary>
    public bool HasRichContent => _ranges.Length > 0 || _interactions.Length > 0;

    /// <summary>
    /// 分段端点集合：{0, Length} ∪ 两表全部端点，去重升序。颜色/payload 的值不影响该集合。
    /// 这是排版分段的唯一模型来源（RT-02 消费）。
    /// </summary>
    internal IReadOnlyList<int> SegmentBoundaries => _segmentBoundaries;

    /// <summary>每段几何样式签名（与 <see cref="SegmentBoundaries"/> 相邻端点对一一对应）。</summary>
    internal IReadOnlyList<StyledTextSegment> Segments => _segments;

    /// <summary>
    /// 分段拓扑 + 每段几何样式签名的预计算哈希：与 <see cref="SegmentBoundaries"/>/
    /// <see cref="Segments"/> 的内容一一对应，不含颜色与 payload 的值。RT-02 的 GeometryKey
    /// 以此为基础；同拓扑仅换色/换 payload 的两实例该值相等。
    /// </summary>
    internal int TopologyHashCode => _topologyHashCode;

    /// <summary>两段拓扑（端点集合 + 每段几何签名）是否相等。</summary>
    internal bool TopologyEquals(StyledText? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        var a = _segments;
        var b = other._segments;
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>plain 快速路径：无区间的纯文本。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 null。</exception>
    public static StyledText Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? Empty : new StyledText(text, [], []);
    }

    /// <summary>构造富文本模型；乱序输入先排序，违反契约抛 <see cref="ArgumentException"/>。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 null。</exception>
    /// <exception cref="ArgumentException">
    /// 区间为空、越界、表内重叠、端点不在字素边界上，或样式的 <see cref="TextStyle.Size"/> 非有限正值。
    /// </exception>
    public StyledText(string text, IEnumerable<TextStyleRange>? ranges, IEnumerable<TextInteractionRange>? interactions)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;

        var rangeArray = SnapshotRanges(ranges, text);
        var interactionArray = SnapshotInteractions(interactions, text);

        var boundaries = BuildBoundaries(text.Length, rangeArray, interactionArray);
        ValidateGraphemeBoundaries(text, boundaries);

        _ranges = rangeArray;
        _interactions = interactionArray;
        _segmentBoundaries = boundaries;
        _segments = BuildSegments(boundaries, rangeArray);
        Ranges = new ReadOnlyCollection<TextStyleRange>(rangeArray);
        Interactions = new ReadOnlyCollection<TextInteractionRange>(interactionArray);
        _hashCode = ComputeHash();
        _topologyHashCode = ComputeTopologyHash();
    }

    private static TextStyleRange[] SnapshotRanges(IEnumerable<TextStyleRange>? ranges, string text)
    {
        var array = ranges?.ToArray() ?? [];
        Array.Sort(array, static (a, b) => a.Start != b.Start ? a.Start - b.Start : a.End - b.End);

        for (var i = 0; i < array.Length; i++)
        {
            var r = array[i];
            if (r.Start < 0 || r.End > text.Length)
            {
                throw new ArgumentException($"样式区间越界：[{r.Start},{r.End}) 超出文本长度 {text.Length}。", nameof(ranges));
            }

            if (r.End <= r.Start)
            {
                throw new ArgumentException($"样式区间为空或逆序：[{r.Start},{r.End})。", nameof(ranges));
            }

            if (i > 0 && r.Start < array[i - 1].End)
            {
                throw new ArgumentException($"样式区间重叠：[{array[i - 1].Start},{array[i - 1].End}) 与 [{r.Start},{r.End})。", nameof(ranges));
            }

            if (r.Style is { Size: { } size } && (!double.IsFinite(size) || size <= 0))
            {
                throw new ArgumentException($"样式字号必须是有限正值，得到 {size}。", nameof(ranges));
            }

            array[i] = r with { Style = SnapshotStyle(r.Style) };
        }

        return array;
    }

    private static TextInteractionRange[] SnapshotInteractions(IEnumerable<TextInteractionRange>? interactions, string text)
    {
        var array = interactions?.ToArray() ?? [];
        Array.Sort(array, static (a, b) => a.Start != b.Start ? a.Start - b.Start : a.End - b.End);

        for (var i = 0; i < array.Length; i++)
        {
            var r = array[i];
            if (r.Start < 0 || r.End > text.Length)
            {
                throw new ArgumentException($"交互区间越界：[{r.Start},{r.End}) 超出文本长度 {text.Length}。", nameof(interactions));
            }

            if (r.End <= r.Start)
            {
                throw new ArgumentException($"交互区间为空或逆序：[{r.Start},{r.End})。", nameof(interactions));
            }

            if (i > 0 && r.Start < array[i - 1].End)
            {
                throw new ArgumentException($"交互区间重叠：[{array[i - 1].Start},{array[i - 1].End}) 与 [{r.Start},{r.End})。", nameof(interactions));
            }
        }
        return array;
    }

    /// <summary>
    /// 把 <see cref="TextStyle.Family"/> 的 <see cref="FontFamily.Fallbacks"/> 冻结为独立只读副本，
    /// 使调用方持有底层可变引用也不影响本模型。不改动 <see cref="FontFamily"/> 的公开语义。
    /// </summary>
    private static TextStyle? SnapshotStyle(TextStyle? style) => style switch
    {
        { Family: { Fallbacks.Count: > 0 } family } => style with { Family = family.FrozenSnapshot() },
        _ => style,
    };

    private static int[] BuildBoundaries(int length, TextStyleRange[] ranges, TextInteractionRange[] interactions)
    {
        var set = new SortedSet<int> { 0, length };
        foreach (var r in ranges)
        {
            set.Add(r.Start);
            set.Add(r.End);
        }

        foreach (var r in interactions)
        {
            set.Add(r.Start);
            set.Add(r.End);
        }

        var boundaries = new int[set.Count];
        set.CopyTo(boundaries);
        return boundaries;
    }

    private static void ValidateGraphemeBoundaries(string text, int[] boundaries)
    {
        // 首尾端点总是边界；全部落首尾时跳过枚举。
        if (boundaries.Length <= 2)
        {
            return;
        }

        // ParseCombiningCharacters 返回每个字素起始偏移的 int[]（一次分配，不枚举 string 子串）；
        // 内部端点合法当且仅当它与某个字素起点重合（0/Length 已在上面的 early return 覆盖）。
        var starts = StringInfo.ParseCombiningCharacters(text);
        for (var i = 1; i < boundaries.Length - 1; i++)
        {
            var offset = boundaries[i];
            if (Array.BinarySearch(starts, offset) < 0)
            {
                throw new ArgumentException($"区间端点 {offset} 不在字素边界上（会切开代理对/组合序列/ZWJ 序列）。", nameof(text));
            }
        }
    }

    private static StyledTextSegment[] BuildSegments(int[] boundaries, TextStyleRange[] ranges)
    {
        var segments = new StyledTextSegment[boundaries.Length - 1];
        var rangeIndex = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var start = boundaries[i];
            while (rangeIndex < ranges.Length && ranges[rangeIndex].End <= start)
            {
                rangeIndex++;
            }

            var style = rangeIndex < ranges.Length && ranges[rangeIndex].Start <= start
                ? ranges[rangeIndex].Style
                : null;

            segments[i] = new StyledTextSegment(
                start,
                boundaries[i + 1],
                new StyledTextFontSignature(style?.Family, style?.Size, style?.Weight, style?.Italic));
        }

        return segments;
    }

    /// <inheritdoc />
    public bool Equals(StyledText? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!string.Equals(Text, other.Text, StringComparison.Ordinal))
        {
            return false;
        }

        if (_ranges.Length != other._ranges.Length || _interactions.Length != other._interactions.Length)
        {
            return false;
        }

        for (var i = 0; i < _ranges.Length; i++)
        {
            if (!_ranges[i].Equals(other._ranges[i]))
            {
                return false;
            }
        }

        for (var i = 0; i < _interactions.Length; i++)
        {
            var a = _interactions[i];
            var b = other._interactions[i];
            if (a.Start != b.Start || a.End != b.End || a.Id != b.Id || !Equals(a.Payload, b.Payload))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as StyledText);

    /// <inheritdoc />
    public override int GetHashCode() => _hashCode;

    /// <summary>结构相等运算符。</summary>
    public static bool operator ==(StyledText? left, StyledText? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>结构不相等运算符。</summary>
    public static bool operator !=(StyledText? left, StyledText? right) => !(left == right);

    private int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(Text, StringComparer.Ordinal);
        foreach (var r in _ranges)
        {
            hash.Add(r.Start);
            hash.Add(r.End);
            hash.Add(r.Style);
        }

        foreach (var r in _interactions)
        {
            hash.Add(r.Start);
            hash.Add(r.End);
            hash.Add(r.Id);
            hash.Add(r.Payload);
        }

        return hash.ToHashCode();
    }

    private int ComputeTopologyHash()
    {
        var hash = new HashCode();
        foreach (var segment in _segments)
        {
            hash.Add(segment.Start);
            hash.Add(segment.End);
            hash.Add(segment.FontSignature);
        }

        return hash.ToHashCode();
    }

    /// <summary>返回把样式区间集合替换为 <paramref name="ranges"/> 的新副本（验证/排序规则同构造）。</summary>
    public StyledText WithRanges(IEnumerable<TextStyleRange> ranges) =>
        new(Text, ranges, _interactions);

    /// <summary>返回把交互区间集合替换为 <paramref name="interactions"/> 的新副本。</summary>
    public StyledText WithInteractions(IEnumerable<TextInteractionRange> interactions) =>
        new(Text, _ranges, interactions);
}

/// <summary>
/// <see cref="StyledText"/> 的便利构建器：累积样式/交互区间后一次构造。
/// 构建器自身可变；产出的 <see cref="StyledText"/> 不可变。
/// </summary>
public sealed class StyledTextBuilder
{
    private readonly List<TextStyleRange> _ranges = [];
    private readonly List<TextInteractionRange> _interactions = [];

    /// <summary>添加样式区间（构造时排序验证；乱序添加合法）。</summary>
    public StyledTextBuilder AddStyle(int start, int end, TextStyle? style)
    {
        _ranges.Add(new TextStyleRange(start, end, style));
        return this;
    }

    /// <summary>添加交互区间。</summary>
    public StyledTextBuilder AddInteraction(int start, int end, int id, object? payload)
    {
        _interactions.Add(new TextInteractionRange(start, end, id, payload));
        return this;
    }

    /// <summary>构造 <see cref="StyledText"/>；区间契约同 <see cref="StyledText(string, IEnumerable{TextStyleRange}?, IEnumerable{TextInteractionRange}?)"/>。</summary>
    public StyledText Build(string text) => new(text, _ranges, _interactions);
}
