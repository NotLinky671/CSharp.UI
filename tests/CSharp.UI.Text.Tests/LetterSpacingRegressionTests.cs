using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>
/// 代码审核返修 R09 的回归测试：字距必须同时影响测量宽度与实际字形间距（栅格化结果）。
/// </summary>
public class LetterSpacingRegressionTests
{
    [Fact]
    public void R09_LetterSpacing_ChangesMeasuredWidthAndRasterizedInkExtent()
    {
        using var service = new SkiaTextService();

        var plain = service.Layout(TextTestHelpers.Request("AB"));
        var spaced = service.Layout(TextTestHelpers.Request("AB", letterSpacing: 12));

        // 测量：字距生效。
        Assert.True(
            spaced.Size.Width > plain.Size.Width + 8,
            $"字距没有增大测量宽度：{plain.Size.Width} -> {spaced.Size.Width}");

        // 绘制：最右着色列同样右移，说明字距不再只是空白。
        var plainInk = RightmostInkedColumn(service.Rasterize(plain));
        var spacedInk = RightmostInkedColumn(service.Rasterize(spaced));

        Assert.True(plainInk > 0, "对照组的位图应当有可见字形");
        Assert.True(
            spacedInk > plainInk,
            $"字距没有改变实际字形间距：最右着色列 {plainInk} -> {spacedInk}");
        Assert.InRange(spacedInk - plainInk, 10, 14);
    }

    [Fact]
    public void R09_LetterSpacing_IsExposedOnLayoutResultAndZeroByDefault()
    {
        using var service = new SkiaTextService();

        Assert.Equal(0, service.Layout(TextTestHelpers.Request("AB")).LetterSpacing, 3);
        Assert.Equal(12, service.Layout(TextTestHelpers.Request("AB", letterSpacing: 12)).LetterSpacing, 3);
    }

    [Fact]
    public void R09_LetterSpacing_MovesCaretMappingConsistentlyWithInk()
    {
        using var service = new SkiaTextService();

        var spaced = service.Layout(TextTestHelpers.Request("AB", letterSpacing: 12));
        var caretB = spaced.GetCaretX(1);
        var caretEnd = spaced.GetCaretX(2);

        // 第二个字素的光标位置相对字形推进量右移了一个字距。
        var plain = service.Layout(TextTestHelpers.Request("AB"));
        Assert.True(caretB > plain.GetCaretX(1) + 8);
        Assert.True(caretEnd > caretB);
    }

    private static int RightmostInkedColumn(PixelBuffer buffer)
    {
        var rowBytes = buffer.Stride;
        var rightmost = -1;
        for (var y = 0; y < buffer.Height; y++)
        {
            var row = buffer.Pixels.AsSpan(y * rowBytes, buffer.Width);
            for (var x = buffer.Width - 1; x > rightmost; x--)
            {
                if (row[x] > 8)
                {
                    rightmost = x;
                    break;
                }
            }
        }

        return rightmost;
    }
}
