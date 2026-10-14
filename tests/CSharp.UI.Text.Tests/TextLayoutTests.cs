using SkiaSharp;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>排版、测量、换行、对齐、省略与字体度量测试。</summary>
public class TextLayoutTests
{
    [Fact]
    public void 中英文混排_不换行时产生单行且尺寸为正()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(TextTestHelpers.MixedText, maxLines: 1));

        Assert.Equal(TextTestHelpers.MixedText, layout.Text);
        Assert.Single(layout.Lines);
        Assert.True(layout.Size.Width > 0, "混排文本宽度应大于 0");
        Assert.True(layout.Size.Height > 0, "混排文本高度应大于 0");
        Assert.False(layout.IsTrimmed);
        Assert.Equal(0, layout.Lines[0].Start);
        Assert.Equal(TextTestHelpers.MixedText.Length, layout.Lines[0].End);
        Assert.Equal(TextTestHelpers.MixedText.Length + 1, layout.CaretStops.Count);
        Assert.Equal(0, layout.CaretXs[0]);
        TextTestHelpers.AssertInvariants(layout);
        TextTestHelpers.AssertCaretXsMonotonicPerLine(layout);
    }

    [Fact]
    public void 混排文本_宽度大于同字号单字符宽度_且随内容增长()
    {
        using var service = new SkiaTextService();
        var shortLayout = service.Layout(TextTestHelpers.Request("ab"));
        var longLayout = service.Layout(TextTestHelpers.Request("abcdef"));

        Assert.True(longLayout.Size.Width > shortLayout.Size.Width);
        Assert.True(shortLayout.Size.Width > TextTestHelpers.BodyFont.Size / 4);
    }

    [Fact]
    public void 纯拉丁文本_宽度与Skia直接测量同一量级()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request("Hello world"));
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, (float)TextTestHelpers.BodyFont.Size);
        var expected = font.MeasureText("Hello world", null);

        // HarfBuzz 会应用 kerning，Skia 的 MeasureText 不会，因此允许 25% 偏差；
        // 该断言主要用于发现 shaping 单位换算错误（例如位置未归一化导致的数倍偏差）。
        Assert.InRange(layout.Size.Width, expected * 0.75, expected * 1.25);
    }

    [Fact]
    public void 中文换行_有限宽度下产生多行且总高度大于单行()
    {
        using var service = new SkiaTextService();
        var single = service.Layout(TextTestHelpers.Request(TextTestHelpers.ChineseText));
        var wrapped = service.Layout(TextTestHelpers.Request(TextTestHelpers.ChineseText, maxWidth: 60, wrapping: TextWrapping.Wrap));

        Assert.True(wrapped.Lines.Count > 1, "CJK 文本应在有限宽度下换行");
        Assert.True(wrapped.Size.Height > single.Size.Height);
        Assert.True(wrapped.Size.Width <= 60 + 0.5, $"换行后宽度不应超过约束：{wrapped.Size.Width}");
        foreach (var line in wrapped.Lines)
        {
            Assert.True(line.Bounds.Width <= 60 + 0.5, $"行宽不应超过约束：{line.Bounds.Width}");
        }

        TextTestHelpers.AssertInvariants(wrapped);
        TextTestHelpers.AssertCaretXsMonotonicPerLine(wrapped);
    }

    [Fact]
    public void 拉丁换行_只在空白处断行()
    {
        const string text = "the quick brown fox jumps over the lazy dog";
        using var service = new SkiaTextService();
        var wrapped = service.Layout(TextTestHelpers.Request(text, maxWidth: 80, wrapping: TextWrapping.Wrap));

        Assert.True(wrapped.Lines.Count > 1);
        for (var i = 1; i < wrapped.Lines.Count; i++)
        {
            var start = wrapped.Lines[i].Start;
            Assert.True(start > 0);
            Assert.Equal(' ', text[start - 1]);
        }

        foreach (var line in wrapped.Lines)
        {
            Assert.True(line.Bounds.Width <= 80 + 0.5, $"行宽不应超过约束：{line.Bounds.Width}");
        }
    }

    [Fact]
    public void 超长单词_在约束内硬断且不丢字()
    {
        const string text = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var service = new SkiaTextService();
        var wrapped = service.Layout(TextTestHelpers.Request(text, maxWidth: 40, wrapping: TextWrapping.Wrap));

        Assert.True(wrapped.Lines.Count > 1);
        Assert.Equal(text, wrapped.Text);
        foreach (var line in wrapped.Lines)
        {
            Assert.True(line.Length > 0, "硬断也必须前进");
        }

        TextTestHelpers.AssertInvariants(wrapped);
    }

    [Fact]
    public void 对齐_中间与右对齐的首行起点不同于左对齐()
    {
        const string text = "aa\nbbbbbbbb";
        using var service = new SkiaTextService();
        var left = service.Layout(TextTestHelpers.Request(text));
        var center = service.Layout(TextTestHelpers.Request(text, alignment: TextAlignment.Center));
        var right = service.Layout(TextTestHelpers.Request(text, alignment: TextAlignment.Right));

        Assert.Equal(2, left.Lines.Count);
        Assert.True(left.Lines[0].Bounds.Width < left.Lines[1].Bounds.Width, "首行应比第二行窄");

        Assert.Equal(0, left.Lines[0].Bounds.X);
        Assert.True(center.Lines[0].Bounds.X > left.Lines[0].Bounds.X, "居中对齐首行起点应右移");
        Assert.True(right.Lines[0].Bounds.X > center.Lines[0].Bounds.X, "右对齐首行起点应更靠右");
        Assert.Equal(right.Size.Width, right.Lines[0].Bounds.X + right.Lines[0].Bounds.Width, 3);

        // 行内光标随对齐整体平移。
        Assert.True(center.GetCaretX(0) > left.GetCaretX(0));
        Assert.Equal(center.Lines[0].Bounds.X, center.GetCaretX(0), 3);
    }

    [Fact]
    public void 省略_有限宽度下单行被截断并以省略号结尾()
    {
        const string text = "这是一个很长的中文文本需要被省略号截断";
        const double maxWidth = 80;
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(
            text,
            maxWidth: maxWidth,
            trimming: TextTrimming.Ellipsis,
            maxLines: 1));

        Assert.True(layout.IsTrimmed);
        Assert.EndsWith("…", layout.Text);
        Assert.True(layout.Text.Length < text.Length);
        Assert.True(layout.Size.Width <= maxWidth + 0.5, $"省略后宽度不应超过约束：{layout.Size.Width}");
        Assert.StartsWith(text[..(layout.Text.Length - 1)], layout.Text);
        TextTestHelpers.AssertInvariants(layout);

        // 不省略时保持原始文本并可超出约束。
        var overflow = service.Layout(TextTestHelpers.Request(text, maxWidth: maxWidth, maxLines: 1));
        Assert.False(overflow.IsTrimmed);
        Assert.Equal(text, overflow.Text);
        Assert.True(overflow.Size.Width > maxWidth);
    }

    [Fact]
    public void 省略_换行配合最大行数时末行加省略号()
    {
        const string text = TextTestHelpers.ChineseText;
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(
            text,
            maxWidth: 60,
            wrapping: TextWrapping.Wrap,
            trimming: TextTrimming.Ellipsis,
            maxLines: 2));

        Assert.True(layout.IsTrimmed);
        Assert.Equal(2, layout.Lines.Count);
        Assert.EndsWith("…", layout.Text);
        Assert.True(layout.Size.Width <= 60 + 0.5);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 最大行数_不省略时隐藏多余行并标记为已隐藏()
    {
        const string text = "aa\nbbbbbbbb";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text, maxLines: 1));

        Assert.Single(layout.Lines);
        Assert.True(layout.IsTrimmed);
        Assert.Equal("aa", layout.Text);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 显式行高_行高与总高度按配置生效()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request("a\nb", lineHeight: 40));

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(80, layout.Size.Height, 3);
        Assert.Equal(40, layout.Lines[0].Bounds.Height, 3);
        Assert.Equal(40, layout.Lines[1].Bounds.Top, 3);
        Assert.True(layout.Lines[0].Baseline > 0 && layout.Lines[0].Baseline < 40);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 字距_按字素数量增加宽度()
    {
        using var service = new SkiaTextService();
        var plain = service.Layout(TextTestHelpers.Request("abcd"));
        var spaced = service.Layout(TextTestHelpers.Request("abcd", letterSpacing: 2));

        // 4 个字素 → 增加 4 × 2 逻辑像素。
        Assert.Equal(plain.Size.Width + 8, spaced.Size.Width, 3);
        Assert.Equal(0, spaced.CaretXs[0], 3);
        Assert.Equal(spaced.Size.Width, spaced.CaretXs[^1], 3);
        TextTestHelpers.AssertInvariants(spaced);
    }

    [Fact]
    public void 字体度量_各项为正且行高等于默认行高()
    {
        using var service = new SkiaTextService();
        var metrics = service.GetFontMetrics(TextTestHelpers.BodyFont);

        Assert.True(metrics.Ascent > 0, $"ascent 应为正数：{metrics.Ascent}");
        Assert.True(metrics.Descent > 0, $"descent 应为正数：{metrics.Descent}");
        Assert.True(metrics.LineGap >= 0, $"line gap 不应为负：{metrics.LineGap}");
        Assert.True(metrics.LineHeight > 0);

        var layout = service.Layout(TextTestHelpers.Request("Ag"));
        Assert.Equal(metrics.LineHeight, layout.Size.Height, 2);
    }

    [Fact]
    public void 空文本_保留一个行高的高度且只有一个光标位置()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(string.Empty));

        Assert.Equal(string.Empty, layout.Text);
        Assert.Equal(0, layout.Size.Width, 3);
        Assert.True(layout.Size.Height > 0);
        Assert.Single(layout.CaretStops);
        Assert.Equal(0, layout.CaretStops[0]);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 段落换行_按换行符分行且行范围连续()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request("第一行\nsecond\r\n第三行"));

        Assert.Equal(3, layout.Lines.Count);
        // 行范围包含段落末尾的换行符，便于索引连续覆盖原文。
        Assert.Equal("第一行", layout.Text[layout.Lines[0].Start..layout.Lines[0].End].TrimEnd('\r', '\n'));
        Assert.Equal("second", layout.Text[layout.Lines[1].Start..layout.Lines[1].End].TrimEnd('\r', '\n'));
        Assert.Equal("第三行", layout.Text[layout.Lines[2].Start..layout.Lines[2].End]);
        TextTestHelpers.AssertInvariants(layout);
        TextTestHelpers.AssertCaretXsMonotonicPerLine(layout);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void 极窄宽度_换行仍然终止且每行至少一个字素(double maxWidth)
    {
        const string text = "中文 wrap mixed 文本";
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(text, maxWidth: maxWidth, wrapping: TextWrapping.Wrap));

        Assert.Equal(text, layout.Text);
        Assert.True(layout.Lines.Count > 1);
        foreach (var line in layout.Lines)
        {
            Assert.True(line.Length > 0, "极窄宽度下每行也必须前进");
        }

        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void 最大行数为零_视为不限制行数()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request("a\nb", maxLines: 0));

        Assert.Equal(2, layout.Lines.Count);
        Assert.False(layout.IsTrimmed);
        TextTestHelpers.AssertInvariants(layout);
    }

    [Fact]
    public void DPI缩放_不改变逻辑尺寸但参与栅格化()
    {
        using var service = new SkiaTextService();
        var single = service.Layout(TextTestHelpers.Request("Ag"));
        var doubled = service.Layout(TextTestHelpers.Request("Ag", pixelScale: 2));

        Assert.Equal(single.Size.Width, doubled.Size.Width, 3);
        Assert.Equal(single.Size.Height, doubled.Size.Height, 3);
        Assert.Equal(2, doubled.PixelScale);

        var raster = service.Rasterize(doubled);
        Assert.Equal((int)Math.Ceiling(doubled.Size.Width * 2), raster.Width);
        Assert.Equal((int)Math.Ceiling(doubled.Size.Height * 2), raster.Height);
    }
}
