using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>测试共用的字体描述、不变量断言与临时文件工具。</summary>
internal static class TextTestHelpers
{
    /// <summary>测试正文字体：平台默认字体、20 逻辑像素。</summary>
    public static readonly FontDescription BodyFont = new(FontFamily.Default, 20);

    /// <summary>中文示例文本（用于混排、换行与回退断言）。</summary>
    public const string ChineseText = "这是一段比较长的中文文本用于验证换行行为";

    /// <summary>混合中英文与数字的示例文本。</summary>
    public const string MixedText = "计数：12 次";

    /// <summary>构造排版请求。</summary>
    public static TextLayoutRequest Request(
        string text,
        double maxWidth = double.PositiveInfinity,
        TextWrapping wrapping = TextWrapping.NoWrap,
        TextTrimming trimming = TextTrimming.None,
        TextAlignment alignment = TextAlignment.Left,
        int maxLines = int.MaxValue,
        double lineHeight = 0,
        double pixelScale = 1,
        double letterSpacing = 0,
        FontDescription? font = null)
        => new(text)
        {
            Font = font ?? BodyFont,
            MaxWidth = maxWidth,
            Wrapping = wrapping,
            Trimming = trimming,
            Alignment = alignment,
            MaxLines = maxLines,
            LineHeight = lineHeight,
            PixelScale = pixelScale,
            LetterSpacing = letterSpacing,
        };

    /// <summary>
    /// 断言 <see cref="TextLayoutResult"/> 的全部公开不变量：
    /// 行范围连续覆盖可见文本、CaretStops 严格递增且首 0 末 Text.Length、CaretXs 与之一一对应。
    /// </summary>
    public static void AssertInvariants(TextLayoutResult layout)
    {
        Assert.NotNull(layout.Text);
        Assert.NotEmpty(layout.Lines);

        // 行：连续覆盖 [0, Text.Length)
        Assert.Equal(0, layout.Lines[0].Start);
        for (var i = 1; i < layout.Lines.Count; i++)
        {
            Assert.Equal(layout.Lines[i - 1].End, layout.Lines[i].Start);
            Assert.True(layout.Lines[i].Bounds.Top > layout.Lines[i - 1].Bounds.Top);
        }

        Assert.Equal(layout.Text.Length, layout.Lines[^1].End);
        foreach (var line in layout.Lines)
        {
            Assert.InRange(line.Start, 0, layout.Text.Length);
            Assert.InRange(line.End, line.Start, layout.Text.Length);
            Assert.True(line.Bounds.Width >= 0);
            Assert.True(line.Bounds.Height > 0);
            Assert.True(line.Baseline > line.Bounds.Top);
            Assert.True(line.Baseline <= line.Bounds.Bottom + (layout.Font.Size * 2));
        }

        // 字素边界：严格递增、首 0 末 Text.Length、不与 CaretXs 错位
        Assert.NotEmpty(layout.CaretStops);
        Assert.Equal(layout.CaretStops.Count, layout.CaretXs.Count);
        Assert.Equal(0, layout.CaretStops[0]);
        Assert.Equal(layout.Text.Length, layout.CaretStops[^1]);
        for (var i = 1; i < layout.CaretStops.Count; i++)
        {
            Assert.True(layout.CaretStops[i] > layout.CaretStops[i - 1]);
        }

        foreach (var x in layout.CaretXs)
        {
            Assert.False(double.IsNaN(x));
            Assert.True(x >= -0.001, $"光标 X 不应为负：{x}");
        }

        // 每个字素边界都自洽：吸附后不变，且能反查 X
        foreach (var stop in layout.CaretStops)
        {
            Assert.Equal(stop, layout.SnapToCaretStop(stop));
            Assert.True(layout.GetCaretX(stop) >= -0.001);
            Assert.True(layout.GetLineForIndex(stop).Start <= stop);
        }

        // 尺寸与行高一致
        var lineHeight = layout.Lines[0].Bounds.Height;
        foreach (var line in layout.Lines)
        {
            Assert.Equal(lineHeight, line.Bounds.Height, 3);
        }

        Assert.Equal(layout.Lines.Count * lineHeight, layout.Size.Height, 3);
        Assert.True(layout.Size.Width >= 0);
    }

    /// <summary>断言每一行内的光标 X 单调不减（行边界处的索引归属该行）。</summary>
    public static void AssertCaretXsMonotonicPerLine(TextLayoutResult layout)
    {
        for (var lineIndex = 0; lineIndex < layout.Lines.Count; lineIndex++)
        {
            var line = layout.Lines[lineIndex];
            var previous = double.NegativeInfinity;
            for (var i = 0; i < layout.CaretStops.Count; i++)
            {
                var stop = layout.CaretStops[i];
                if (layout.GetLineForIndex(stop) != line)
                {
                    continue;
                }

                Assert.True(layout.CaretXs[i] >= previous - 0.001, $"第 {lineIndex} 行的光标 X 应单调不减：{previous} -> {layout.CaretXs[i]}");
                previous = layout.CaretXs[i];
            }
        }
    }

    /// <summary>
    /// 测试专用暂存目录：固定在测试输出目录下、按进程隔离，避免依赖系统 Temp
    /// （受限策略下测试宿主对系统 Temp 无写权限时会产生 UnauthorizedAccess）。
    /// </summary>
    private static readonly string ScratchRoot = CreateScratchRoot();

    private static string CreateScratchRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "scratch", $"{Environment.ProcessId:x}-{Guid.NewGuid():N}");
        return Directory.CreateDirectory(root).FullName;
    }

    /// <summary>生成一个位于测试暂存目录中的唯一路径（文件本身不创建）。</summary>
    public static string UniqueTempPath(string extension) => Path.Combine(ScratchRoot, $"csharp-ui-text-{Guid.NewGuid():N}{extension}");
}
