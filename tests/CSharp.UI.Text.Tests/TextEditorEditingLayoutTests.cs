using CSharp.UI;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>
/// TE-01 真实 Skia 排版：Editing 模式的尾随空段、软换行共享 offset 双坐标、
/// 行尾空白未裁剪 caret X、硬换行行界与跨行选区矩形。
/// </summary>
public sealed class TextEditorEditingLayoutTests
{
    private static TextLayoutResult Layout(
        string text,
        double maxWidth = double.PositiveInfinity,
        TextWrapping wrapping = TextWrapping.NoWrap,
        bool editing = true)
    {
        using var service = new SkiaTextService();
        var request = new TextLayoutRequest(text)
        {
            Font = TextTestHelpers.BodyFont,
            MaxWidth = maxWidth,
            Wrapping = wrapping,
            Editing = editing,
        };
        return service.Layout(request);
    }

    [Fact]
    public void Editing_TrailingNewlineProducesEmptyLastLineWithCaret()
    {
        var layout = Layout("a\n");

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(2, layout.Lines[1].Start);
        Assert.Equal(2, layout.Lines[1].End);
        Assert.Equal(0, layout.Lines[1].Bounds.Width);

        // 末尾空行 caret 可定位：offset 2 的 caret 矩形在第二行。
        var caret = layout.GetCaretBounds(2, TextAffinity.LineStart);
        Assert.Equal(layout.Lines[1].Bounds.Y, caret.Y, 3);
        Assert.Equal(layout.Lines[1].Bounds.Height, caret.Height, 3);
    }

    [Fact]
    public void Editing_SoftWrapSharedOffsetHasDualCaretPositions()
    {
        // 强制在两字宽处折行：拉丁字母宽约 font.Size*0.5，给窄 maxWidth 触发 wrap。
        var layout = Layout("abcd", maxWidth: TextTestHelpers.BodyFont.Size * 1.2, wrapping: TextWrapping.Wrap);

        Assert.Equal(2, layout.Lines.Count);
        var sharedStop = layout.Lines[0].End;
        Assert.Equal(sharedStop, layout.Lines[1].Start); // 软折行点共享 offset
        Assert.True(sharedStop > 0 && sharedStop < layout.Text.Length);
        Assert.False(layout.Text[sharedStop - 1] is '\n' or '\r'); // 非硬换行

        var upper = layout.CaretsByLine[0].Single(c => c.Stop == sharedStop);
        var lower = layout.CaretsByLine[1].Single(c => c.Stop == sharedStop);

        Assert.Equal(0, upper.LineIndex);
        Assert.Equal(layout.Lines[0].Bounds.Right, upper.X, 3); // 上行末 X = 行内容宽（右缘）
        Assert.Equal(1, lower.LineIndex);
        Assert.Equal(layout.Lines[1].Bounds.Left, lower.X, 3); // 下行首 X = OffsetX（左缘）
    }

    [Fact]
    public void SoftWrapSharedOffset_AffinityResolvesConsistentLineXAndY()
    {
        // R7：软 wrap 共享 offset 的三元归属——LineEnd/Unspecified 归上行（X=上行内容宽、Y=上行 Y、lineIndex=上行），
        // LineStart 归下行；三者必须一致（修复前 LineEnd 的 X 取上行但 Y/lineIndex 混归下行）。
        var layout = Layout("abcd", maxWidth: TextTestHelpers.BodyFont.Size * 1.2, wrapping: TextWrapping.Wrap);
        var sharedStop = layout.Lines[0].End;
        Assert.Equal(sharedStop, layout.Lines[1].Start);

        var upperX = layout.CaretsByLine[0].Single(c => c.Stop == sharedStop).X;
        var lowerX = layout.CaretsByLine[1].Single(c => c.Stop == sharedStop).X;

        foreach (var aff in new[] { TextAffinity.Unspecified, TextAffinity.LineEnd })
        {
            Assert.Equal(0, layout.LineIndexForIndex(sharedStop, aff));
            Assert.Equal(upperX, layout.GetCaretX(sharedStop, aff), 3);
            var bounds = layout.GetCaretBounds(sharedStop, aff);
            Assert.Equal(upperX, bounds.X, 3);
            Assert.Equal(layout.Lines[0].Bounds.Y, bounds.Y, 3);
        }

        Assert.Equal(1, layout.LineIndexForIndex(sharedStop, TextAffinity.LineStart));
        Assert.Equal(lowerX, layout.GetCaretX(sharedStop, TextAffinity.LineStart), 3);
        var lowerBounds = layout.GetCaretBounds(sharedStop, TextAffinity.LineStart);
        Assert.Equal(lowerX, lowerBounds.X, 3);
        Assert.Equal(layout.Lines[1].Bounds.Y, lowerBounds.Y, 3);
    }

    [Fact]
    public void HardBreakPostOffset_ResolvesToLowerLineForAllAffinities()
    {
        // R5+R7：硬换行后 offset 任何 affinity 唯一下行（X/Y/lineIndex 一致归行 1）。
        var layout = Layout("ab\ncd");
        var postBreak = layout.Lines[1].Start; // '\n' 后 offset
        Assert.Equal(3, postBreak);

        var lowerX = layout.CaretsByLine[1].Single(c => c.Stop == postBreak).X;
        foreach (var aff in new[] { TextAffinity.Unspecified, TextAffinity.LineEnd, TextAffinity.LineStart })
        {
            Assert.Equal(1, layout.LineIndexForIndex(postBreak, aff));
            Assert.Equal(lowerX, layout.GetCaretX(postBreak, aff), 3);
            Assert.Equal(layout.Lines[1].Bounds.Y, layout.GetCaretBounds(postBreak, aff).Y, 3);
        }
    }

    [Fact]
    public void Editing_HardNewlineOffsetIsNotSharedAcrossLines()
    {
        var layout = Layout("ab\ncd");

        Assert.Equal(2, layout.Lines.Count);
        // Skia 行 End 含换行符：L0=[0,3)（\\n@2），L1=[3,5)。
        Assert.Equal(3, layout.Lines[0].End);
        Assert.Equal(3, layout.Lines[1].Start);

        // offset 3（\\n 后行首）只归下行：上行 CaretsByLine 不含 stop=3。
        Assert.DoesNotContain(layout.CaretsByLine[0], c => c.Stop == 3);
        Assert.Contains(layout.CaretsByLine[1], c => c.Stop == 3 && c.LineIndex == 1);
        // 上行可停留行尾是 'b' 后（stop=2）。
        Assert.Equal(2, layout.LineEndIndex(0));
        Assert.Equal(3, layout.LineStartIndex(1));
    }

    [Fact]
    public void Editing_TrailingWhitespaceCaretUsesUntrimmedX()
    {
        // 行尾空格后的 caret：X 按未裁剪内容推进（不被 TrimEnd 夹紧到非空白前缀）。
        var layout = Layout("a \n");

        // offset 2 = 'a'+空格 后、'\n' 前。
        var caretX = layout.GetCaretX(2, TextAffinity.LineEnd);
        var spaceAdvance = layout.GetCaretX(2, TextAffinity.LineEnd) - layout.GetCaretX(1, TextAffinity.LineEnd);
        Assert.True(caretX > layout.GetCaretX(1), "行尾空白 caret X 应超过 'a' 后位置");
        Assert.True(spaceAdvance > 0, "空格应贡献水平推进");
    }

    [Fact]
    public void HitTestIndexResolvesAffinityAtSoftWrapSharedOffset()
    {
        var layout = Layout("abcd", maxWidth: TextTestHelpers.BodyFont.Size * 1.2, wrapping: TextWrapping.Wrap);
        var sharedStop = layout.Lines[0].End;

        // 命中上行行尾 → LineEnd。
        var hitUpper = layout.HitTestLine(0, layout.Lines[0].Bounds.Right, out var affUpper);
        Assert.Equal(sharedStop, hitUpper);
        Assert.Equal(TextAffinity.LineEnd, affUpper);

        // 命中下行行首 → LineStart。
        var hitLower = layout.HitTestLine(1, layout.Lines[1].Bounds.Left, out var affLower);
        Assert.Equal(sharedStop, hitLower);
        Assert.Equal(TextAffinity.LineStart, affLower);
    }

    [Fact]
    public void GetRangeRects_SelectionAcrossHardNewlineUsesLineLocalXNotNextLineStart()
    {
        // "abcd\\nx"：选 [0,6)（含 '\\n' 与 'x'）。第一行矩形右缘应是 'd' 后真实 X
        // （行内容宽右缘），不能用 end=6 或下行 start=5 的 X 得到负宽/反转。
        var layout = Layout("abcd\nx");
        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(5, layout.Lines[1].Start); // 'x' 在 offset 5（\\n@4 后）

        var rects = layout.GetRangeRects(0, 6);

        var line0 = layout.Lines[0];
        var line1 = layout.Lines[1];
        // 第一行内容 rect：[0, LineEndIndex(0)]='d'后真实 X。
        var first = rects[0];
        Assert.Equal(line0.Bounds.Y, first.Y, 3);
        Assert.Equal(line0.Bounds.X, first.X, 3);
        Assert.Equal(line0.Bounds.Right, first.Right, 3);
        Assert.True(first.Width > 0);

        // 第二行 'x' 被选中（start=5 ∈ [0,6)）。
        var second = rects[^1];
        Assert.Equal(line1.Bounds.Y, second.Y, 3);
        Assert.Equal(line1.Bounds.X, second.X, 3);
        Assert.True(second.Width > 0);
    }

    [Fact]
    public void GetRangeRects_EndAtNextLineStartDoesNotMisselect()
    {
        var layout = Layout("abcd\nx");

        // 选 [0,5) 含 offsets 0..4（'abcd'+'\\n'），不含 'x'(offset 5)：第二行不产内容 rect。
        var rects = layout.GetRangeRects(0, 5);
        Assert.DoesNotContain(rects, r => r.Y == layout.Lines[1].Bounds.Y && r.Width > 0);
        Assert.Contains(rects, r => r.Y == layout.Lines[0].Bounds.Y && r.Width > 0);

        // 选 [0,6) 含第二行 'x'。
        var rectsWithSecond = layout.GetRangeRects(0, 6);
        Assert.Contains(rectsWithSecond, r => r.Y == layout.Lines[1].Bounds.Y && r.Width > 0);
    }

    [Fact]
    public void GetRangeRects_EmptyRangeProducesNoRect()
    {
        var layout = Layout("ab\ncd");
        var rects = layout.GetRangeRects(2, 2);
        Assert.Empty(rects); // 空选区不产 marker
    }

    [Fact]
    public void GetRangeRects_EmptyLineSelectedByNewlineRangeProducesZeroWidthMarker()
    {
        var layout = Layout("a\n\nb");
        // 第二行是空行 [2,2)；选 [1,3) 跨过它（覆盖其 \n 边界）。
        var rects = layout.GetRangeRects(1, 3);
        Assert.Contains(rects, r => r.Width == 0 && r.Height > 0 && r.Y == layout.Lines[1].Bounds.Y);
    }

    [Fact]
    public void GetRangeRects_NewlineMarkerAppendedWhenSelectionCoversHardBreak()
    {
        var layout = Layout("ab\ncd");
        // 选 [0,3)：覆盖第一行 'ab' + '\n'（offset 2），行尾应追加零宽 newline marker。
        var rects = layout.GetRangeRects(0, 3);
        // 第一行内容 rect + 行尾 newline marker。
        Assert.True(rects.Count >= 2);
        var line0 = layout.Lines[0];
        Assert.Contains(rects, r => r.Y == line0.Bounds.Y && r.Width > 0);
        Assert.Contains(rects, r => r.Y == line0.Bounds.Y && r.Width == 0 && r.X >= line0.Bounds.Right - 0.01);
    }

    [Fact]
    public void Editing_CrlfIsTreatedAsSingleHardBreak()
    {
        // Editing 文本预期已归一化（SanitizeMultiLine），但 builder 需稳健：
        // 若传入 \r\n，SplitParagraphs 把 \r\n 当一个换行处理，行界不错乱。
        var layout = Layout("ab\r\ncd", editing: true);
        Assert.Equal(2, layout.Lines.Count);
        // Skia 行 End 含 \\r\\n terminator：L0=[0,4)，L1=[4,6)。
        Assert.Equal(4, layout.Lines[0].End);
        Assert.Equal(4, layout.Lines[1].Start);
        Assert.Equal(2, layout.LineEndIndex(0)); // 'b' 后、'\\r' 前
        Assert.Equal(4, layout.LineStartIndex(1));
    }

    [Fact]
    public void Editing_CaretXDiffersFromDisplayLayoutForSameText()
    {
        // 同一文本 Editing vs 非 Editing：Editing 的行尾空白 caret X 用未裁剪值，
        // 非 Editing 用 TrimEnd 夹紧——真 Skia 中尾随空格宽可实测不同。
        var editing = Layout("a \n", editing: true);
        var display = Layout("a \n", editing: false);

        var editingX = editing.GetCaretX(2, TextAffinity.LineEnd);
        var displayX = display.GetCaretX(2);

        // Editing X 应 >= 显示 X（空格推进未被夹紧）；真字体空格宽 > 0。
        Assert.True(editingX >= displayX, $"Editing X {editingX} 应不小于 display X {displayX}");
    }
}
