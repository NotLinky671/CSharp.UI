using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>字素边界、代理对、组合字符、命中测试与光标位置映射测试。</summary>
public class GraphemeMappingTests
{
    /// <summary>断言没有把代理对或组合字符从中间拆开。</summary>
    private static void AssertNoSplitClusters(string text, TextLayoutResult layout)
    {
        foreach (var stop in layout.CaretStops)
        {
            if (stop >= text.Length)
            {
                continue;
            }

            // 落在低代理上说明拆开了代理对。
            Assert.False(char.IsLowSurrogate(text[stop]), $"索引 {stop} 落在代理对中间");
        }

        Assert.Equal(0, layout.CaretStops[0]);
        Assert.Equal(text.Length, layout.CaretStops[^1]);
    }

    [Fact]
    public void 代理对_emoji不被拆开()
    {
        const string text = "a😀b";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        // 😀 占 UTF-16 索引 1、2，中间索引 2 不能成为光标位置。
        Assert.Equal(new[] { 0, 1, 3, 4 }, layout.CaretStops);
        Assert.DoesNotContain(2, layout.CaretStops);
        Assert.Equal(1, layout.SnapToCaretStop(2));
        Assert.Equal(3, layout.SnapToCaretStop(2, roundUp: true));
        Assert.True(layout.GetCaretX(3) > layout.GetCaretX(1), "emoji 应占据正宽度");
        AssertNoSplitClusters(text, layout);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 组合字符_基字符与重音符号视为一个字素()
    {
        const string text = "e\u0301";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        Assert.Equal(new[] { 0, 2 }, layout.CaretStops);
        Assert.DoesNotContain(1, layout.CaretStops);
        Assert.Equal(0, layout.SnapToCaretStop(1));
        Assert.Equal(2, layout.SnapToCaretStop(1, roundUp: true));
        AssertNoSplitClusters(text, layout);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 零宽连接序列_家庭emoji整簇处理()
    {
        const string text = "👨‍👩‍👧";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        Assert.Equal(8, text.Length);
        AssertNoSplitClusters(text, layout);
        Assert.Equal(0, layout.CaretStops[0]);
        Assert.Equal(text.Length, layout.CaretStops[^1]);

        // StringInfo 把该序列视为一个字素簇时，只有一个内部边界。
        Assert.Equal(2, layout.CaretStops.Count);
        Assert.Single(layout.Lines);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 命中测试_行首行尾与中间返回字素边界()
    {
        const string text = "abcdef";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));
        var line = layout.Lines[0];
        var middleY = line.Bounds.Y + (line.Bounds.Height / 2);

        Assert.Equal(0, layout.HitTestIndex(new Point(-20, middleY)));
        Assert.Equal(0, layout.HitTestIndex(new Point(0, middleY)));
        Assert.Equal(text.Length, layout.HitTestIndex(new Point(line.Bounds.Right + 40, middleY)));

        var middle = layout.GetCaretX(3);
        var hit = layout.HitTestIndex(new Point(middle, middleY));
        Assert.Equal(3, hit);
        Assert.Equal(hit, layout.SnapToCaretStop(hit));

        // 行外（上方/下方）也应吸附到最近的行。
        Assert.Equal(0, layout.HitTestIndex(new Point(-5, -50)));
        Assert.Equal(text.Length, layout.HitTestIndex(new Point(line.Bounds.Right + 40, line.Bounds.Bottom + 50)));
    }

    [Fact]
    public void 命中测试_emoji内部不会返回代理对中间索引()
    {
        const string text = "a😀b";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));
        var line = layout.Lines[0];
        var middleY = line.Bounds.Y + (line.Bounds.Height / 2);

        foreach (var offset in new[] { 0.0, 1.0, 5.0, 20.0 })
        {
            var hit = layout.HitTestIndex(new Point(layout.GetCaretX(1) + offset, middleY));
            Assert.NotEqual(2, hit);
            Assert.Contains(hit, layout.CaretStops);
        }
    }

    [Fact]
    public void 光标位置_单行内单调不减()
    {
        const string text = "abcdef 计数";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        var previous = double.NegativeInfinity;
        for (var i = 0; i < layout.CaretStops.Count; i++)
        {
            Assert.True(layout.CaretXs[i] >= previous, $"光标 X 应单调不减：{previous} -> {layout.CaretXs[i]}");
            previous = layout.CaretXs[i];
        }

        Assert.Equal(0, layout.CaretXs[0]);
        Assert.True(layout.CaretXs[^1] > 0);
        Assert.Equal(layout.Size.Width, layout.CaretXs[^1], 3);
    }

    [Fact]
    public void 光标位置_行边界索引归属前一行()
    {
        const string text = "abcdef\ngh";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        var boundaryIndex = layout.CaretStops.ToList().IndexOf(layout.Lines[1].Start);
        Assert.True(boundaryIndex > 0);

        // 软/硬换行处的索引同时是两行的边界；契约规定它归属前一行（与 GetLineForIndex 一致），
        // 因此光标画在上一行末尾，而行内后续索引使用下一行的坐标。
        Assert.Equal(layout.Lines[0].Bounds.Width, layout.CaretXs[boundaryIndex], 3);
        Assert.Equal(layout.Lines[1].Bounds.Width, layout.CaretXs[^1], 3);
        Assert.True(layout.CaretXs[boundaryIndex + 1] < layout.CaretXs[boundaryIndex]);
        TextTestHelpers.AssertCaretXsMonotonicPerLine(layout);
    }

    [Fact]
    public void 选区与光标矩形_基于字素边界返回合理几何()
    {
        const string text = "计数 abc";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        var all = layout.GetRangeBounds(0, text.Length);
        Assert.True(all.Width > 0);
        Assert.True(all.Height > 0);
        Assert.Equal(0, all.X, 3);

        var caret = layout.GetCaretBounds(0);
        Assert.True(caret.Height > 0);
        Assert.Equal(0, caret.X, 3);

        var empty = layout.GetRangeBounds(2, 2);
        Assert.Equal(0, empty.Width, 3);
        Assert.True(empty.Height > 0);
    }

    [Fact]
    public void 吸附_越界索引被钳制到首末字素边界()
    {
        const string text = "a😀b";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text));

        Assert.Equal(0, layout.SnapToCaretStop(-5));
        Assert.Equal(text.Length, layout.SnapToCaretStop(999));
        Assert.Equal(text.Length, layout.SnapToCaretStop(999, roundUp: true));
        Assert.Equal(layout.GetCaretX(text.Length), layout.GetCaretX(999), 3);
    }
}

