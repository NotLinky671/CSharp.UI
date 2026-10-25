using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RT-01：<see cref="StyledText"/> 模型 —— 区间验证（半开/不重叠/越界/字素边界）、
/// 两表正交、值相等与预计算哈希、分段拓扑（颜色/payload 值不可见）、输入快照隔离。
/// </summary>
public class StyledTextTests
{
    // ------------------------------------------------------------------ 构造与 plain 快路径

    [Fact]
    public void Create_PlainText_HasNoRichContentAndEmptyTables()
    {
        var styled = StyledText.Create("hello");

        Assert.Equal("hello", styled.Text);
        Assert.False(styled.HasRichContent);
        Assert.Empty(styled.Ranges);
        Assert.Empty(styled.Interactions);
    }

    [Fact]
    public void EmptyText_AndEmptyTables_AreLegal()
    {
        var styled = new StyledText("", null, null);

        Assert.Equal(string.Empty, styled.Text);
        Assert.False(styled.HasRichContent);
    }

    [Fact]
    public void UnsortedInput_IsNormalizedToAscendingOrder()
    {
        var styled = new StyledText(
            "abcdef",
            [new TextStyleRange(3, 5, new TextStyle { Italic = true }), new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })],
            null);

        Assert.Equal(0, styled.Ranges[0].Start);
        Assert.Equal(3, styled.Ranges[1].Start);
    }

    // ------------------------------------------------------------------ 区间契约

    [Fact]
    public void RejectsEmptyOrReversedRanges()
    {
        Assert.Throws<ArgumentException>(() => new StyledText("abc", [new TextStyleRange(1, 1, null)], null));
        Assert.Throws<ArgumentException>(() => new StyledText("abc", [new TextStyleRange(2, 1, null)], null));
        Assert.Throws<ArgumentException>(() => new StyledText("abc", null, [new TextInteractionRange(0, 0, 1, null)]));
    }

    [Fact]
    public void RejectsOutOfBoundsRanges()
    {
        Assert.Throws<ArgumentException>(() => new StyledText("abc", [new TextStyleRange(-1, 1, null)], null));
        Assert.Throws<ArgumentException>(() => new StyledText("abc", [new TextStyleRange(0, 4, null)], null));
        Assert.Throws<ArgumentException>(() => new StyledText("abc", null, [new TextInteractionRange(0, 5, 1, null)]));
    }

    [Fact]
    public void RejectsOverlappingRangesWithinEachTable()
    {
        Assert.Throws<ArgumentException>(() => new StyledText(
            "abcdef",
            [new TextStyleRange(0, 3, null), new TextStyleRange(2, 5, null)],
            null));

        Assert.Throws<ArgumentException>(() => new StyledText(
            "abcdef",
            null,
            [new TextInteractionRange(0, 4, 1, null), new TextInteractionRange(3, 5, 2, null)]));

        // 共享端点（End == Start）不算重叠。
        var adjacent = new StyledText("abcdef", [new TextStyleRange(0, 3, null), new TextStyleRange(3, 6, null)], null);
        Assert.Equal(2, adjacent.Ranges.Count);
    }

    [Fact]
    public void StyleAndInteractionTables_MayOverlapEachOther()
    {
        var styled = new StyledText(
            "abcdef",
            [new TextStyleRange(0, 4, new TextStyle { Foreground = Color.Red })],
            [new TextInteractionRange(2, 6, 7, new Link("https://a"))]);

        Assert.True(styled.HasRichContent);
        Assert.Single(styled.Ranges);
        Assert.Single(styled.Interactions);
    }

    // ------------------------------------------------------------------ 字素边界

    [Fact]
    public void RejectsEndpointsSplittingSurrogatePairs_CombiningMarks_AndZwjSequences()
    {
        // 代理对：emoji "😀" 占 2 个 UTF-16 code unit。
        var emoji = "a😀b";
        Assert.Throws<ArgumentException>(() => new StyledText(emoji, [new TextStyleRange(1, 2, null)], null));
        var aroundEmoji = new StyledText(emoji, [new TextStyleRange(1, 3, null)], null); // 覆盖完整 emoji
        Assert.Single(aroundEmoji.Ranges);

        // 组合序列："e" + U+0301。
        var combining = "éx";
        Assert.Throws<ArgumentException>(() => new StyledText(combining, [new TextStyleRange(0, 1, null)], null));
        var wholeCluster = new StyledText(combining, [new TextStyleRange(0, 2, null)], null);
        Assert.Single(wholeCluster.Ranges);

        // ZWJ 序列：家庭 emoji。
        var family = "👨‍👩‍👧ok";
        Assert.Throws<ArgumentException>(() => new StyledText(family, null, [new TextInteractionRange(0, 4, 1, null)]));
    }

    [Fact]
    public void CjkText_AcceptsPerCharacterBoundaries()
    {
        var styled = new StyledText("你好世界", [new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })], null);
        Assert.Single(styled.Ranges);
    }

    [Fact]
    public void RejectsInvalidStyleSize()
    {
        Assert.Throws<ArgumentException>(() => new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Size = 0 })], null));
        Assert.Throws<ArgumentException>(() => new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Size = -1 })], null));
        Assert.Throws<ArgumentException>(() => new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Size = double.NaN })], null));
        Assert.Throws<ArgumentException>(() => new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Size = double.PositiveInfinity })], null));
    }

    // ------------------------------------------------------------------ 输入快照隔离

    [Fact]
    public void MutatingInputCollections_AfterConstruction_DoesNotAffectInstance()
    {
        var ranges = new List<TextStyleRange> { new(0, 2, new TextStyle { Italic = true }) };
        var interactions = new List<TextInteractionRange> { new(1, 3, 9, new Link("x")) };
        var styled = new StyledText("abcd", ranges, interactions);

        ranges.Add(new TextStyleRange(2, 4, new TextStyle { Weight = FontWeight.Bold }));
        interactions.Clear();

        Assert.Single(styled.Ranges);
        Assert.Single(styled.Interactions);
        Assert.Equal(1, styled.Interactions[0].Start);
    }

    [Fact]
    public void StyleFontFamily_IsSnapshotIntoModel_IndependentlyOfCallerInstance()
    {
        var family = FontFamily.FromName("Main").WithFallbacks("FallbackA");
        var styled = new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Family = family })], null);

        // 等值语义保持：快照副本与源 FontFamily 值相等。
        Assert.Equal(family, styled.Ranges[0].Style!.Family);
        Assert.Single(styled.Ranges[0].Style!.Family!.Fallbacks);

        // 等值：另行构造的相同样式产生相等模型与相同几何签名。
        var other = new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Family = FontFamily.FromName("Main").WithFallbacks("FallbackA") })], null);
        Assert.Equal(styled, other);
        Assert.True(styled.TopologyEquals(other));
    }

    [Fact]
    public void ExposedSnapshots_CannotBeMutated()
    {
        var styled = new StyledText("ab", [new TextStyleRange(0, 1, null)], [new TextInteractionRange(0, 1, 1, null)]);
        Assert.IsNotType<TextStyleRange[]>(styled.Ranges);
        Assert.IsNotType<TextInteractionRange[]>(styled.Interactions);
    }

    // ------------------------------------------------------------------ 值相等与哈希

    [Fact]
    public void SeparatelyConstructedIdenticalInstances_AreEqual_WithSameHash()
    {
        var a = BuildSample("a", "https://a");
        var b = BuildSample("a", "https://a");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Differences_InTextOrRanges_BreakEquality()
    {
        var baseline = BuildSample("a", "https://a");
        Assert.NotEqual(baseline, new StyledText("abXdef", null, null));
        Assert.NotEqual(baseline, BuildSample("a", "https://other"));
        Assert.NotEqual(baseline, new StyledText(
            "abcdef",
            [new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })],
            [new TextInteractionRange(2, 6, 1, new Link("https://a"))]));
    }

    // ------------------------------------------------------------------ 分段拓扑与几何签名

    [Fact]
    public void Topology_DiffersOnlyByColorOrPayload_IsEqual()
    {
        var red = BuildSample("a", "https://a", Color.Red);
        var blue = BuildSample("a", "https://a", Color.Blue);
        var otherUrl = BuildSample("a", "https://other");

        Assert.Equal(red.TopologyHashCode, blue.TopologyHashCode);
        Assert.True(red.TopologyEquals(blue));
        Assert.Equal(red.TopologyHashCode, otherUrl.TopologyHashCode);
        Assert.True(red.TopologyEquals(otherUrl));

        // 但模型全值相等仍然区分颜色与 payload。
        Assert.NotEqual(red, blue);
        Assert.NotEqual(red, otherUrl);
    }

    [Fact]
    public void Topology_ChangesWhenBoundariesChange_IncludingInteractionOnly()
    {
        var baseline = BuildSample("a", "https://a");
        var shiftedInteraction = new StyledText(
            "abcdef",
            [new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })],
            [new TextInteractionRange(1, 5, 1, new Link("https://a"))]);

        Assert.False(baseline.TopologyEquals(shiftedInteraction));
        Assert.NotEqual(baseline.TopologyHashCode, shiftedInteraction.TopologyHashCode);
    }

    [Fact]
    public void SegmentBoundaries_AreUnionOfAllEndpoints()
    {
        var styled = BuildSample("a", "https://a");
        Assert.Equal(new[] { 0, 2, 6 }, styled.SegmentBoundaries.ToArray());

        var fontChangeMidRange = new StyledText(
            "abcdef",
            [new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })],
            null);
        Assert.Equal(new[] { 0, 2, 6 }, fontChangeMidRange.SegmentBoundaries.ToArray());
        Assert.Equal(FontWeight.Bold, fontChangeMidRange.Segments[0].FontSignature.Weight);
        Assert.Null(fontChangeMidRange.Segments[1].FontSignature.Weight);
    }

    [Fact]
    public void InteractionEndpointInsideStyleRange_DoesNotDropStyleSignature()
    {
        // 回归：同一拓扑 {0,2,4,6}，一份样式覆盖跨交互端点的整段 [0,6) Bold，
        // 另一份 [0,2) Bold + [2,6) 继承 —— 后半段几何签名必须不同
        //（segment 样式取「包含其起点」的 style range，不是「起点恰好等于」）。
        var spanning = new StyledText(
            "abcdef",
            [new TextStyleRange(0, 6, new TextStyle { Weight = FontWeight.Bold })],
            [new TextInteractionRange(2, 4, 1, new Link("x"))]);

        var split = new StyledText(
            "abcdef",
            [new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })],
            [new TextInteractionRange(2, 4, 1, new Link("x"))]);

        Assert.Equal(new[] { 0, 2, 4, 6 }, spanning.SegmentBoundaries.ToArray());
        Assert.Equal(new[] { 0, 2, 4, 6 }, split.SegmentBoundaries.ToArray());

        // spanning：三个 segment 全部带 Bold 签名。
        foreach (var segment in spanning.Segments)
        {
            Assert.Equal(FontWeight.Bold, segment.FontSignature.Weight);
        }

        // split：仅首段 Bold，后两段继承段落默认。
        Assert.Equal(FontWeight.Bold, split.Segments[0].FontSignature.Weight);
        Assert.Null(split.Segments[1].FontSignature.Weight);
        Assert.Null(split.Segments[2].FontSignature.Weight);

        Assert.False(spanning.TopologyEquals(split));
        Assert.NotEqual(spanning.TopologyHashCode, split.TopologyHashCode);
    }

    // ------------------------------------------------------------------ Builder / With*

    [Fact]
    public void Builder_AccumulatesUnsortedRanges()
    {
        var styled = new StyledTextBuilder()
            .AddInteraction(2, 4, 5, new Link("x"))
            .AddStyle(0, 2, new TextStyle { Italic = true })
            .Build("abcd");

        Assert.True(styled.HasRichContent);
        Assert.Single(styled.Ranges);
        Assert.Single(styled.Interactions);
    }

    [Fact]
    public void WithRanges_ReturnsNewInstance_PreservingInteractions()
    {
        var original = new StyledText("abcd", null, [new TextInteractionRange(0, 2, 1, new Link("x"))]);
        var restyled = original.WithRanges([new TextStyleRange(0, 2, new TextStyle { Italic = true })]);

        Assert.NotSame(original, restyled);
        Assert.Single(restyled.Ranges);
        Assert.Single(restyled.Interactions);
        Assert.Empty(original.Ranges);
    }

    private static StyledText BuildSample(string linkId, string url, Color? foreground = null) =>
        new(
            "abcdef",
            [new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold, Foreground = foreground })],
            [new TextInteractionRange(2, 6, linkId.GetHashCode(StringComparison.Ordinal), new Link(url))]);

    private sealed record Link(string Url);
}
