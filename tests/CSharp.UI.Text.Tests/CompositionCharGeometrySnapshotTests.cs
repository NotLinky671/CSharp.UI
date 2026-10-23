using CSharp.UI;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>
/// TE-04：真实 Skia 多行排版的组合字符几何快照 —— charPos 经 affinity 消歧定位真实行坐标；
/// 硬换行归属下行、软换行预编辑字符归下行行首、无预编辑 charPos=0 应答真实 caret。
/// </summary>
public sealed class CompositionCharGeometrySnapshotTests
{
    private static TextLayoutResult Layout(string text, double maxWidth, TextWrapping wrapping = TextWrapping.Wrap)
    {
        using var service = new SkiaTextService();
        return service.Layout(new TextLayoutRequest(text)
        {
            Font = TextTestHelpers.BodyFont,
            MaxWidth = maxWidth,
            Wrapping = wrapping,
            Editing = true,
        });
    }

    private static CompositionCharGeometrySnapshot Snapshot(
        TextLayoutResult layout,
        string preedit,
        int preeditStart,
        int caretIndex,
        TextAffinity affinity = TextAffinity.Unspecified)
        => new()
        {
            Layout = layout,
            Preedit = preedit,
            PreeditStart = preeditStart,
            CaretIndex = caretIndex,
            Origin = new Point(10, 20),
            EditableClip = new Rect(0, 0, 500, 500),
            Epoch = 1,
            DocumentVersion = 0,
            LayoutSerial = 0,
            ScrollX = 0,
            ScrollY = 0,
            Affinity = affinity,
        };

    [Fact]
    public void PreeditCharAtHardBreakStartResolvesToNextLine()
    {
        // 显示串 "a\nXY"：预编辑 "XY" 从显示 offset 2 开始（硬换行之后）。
        // charPos=0（预编辑首字符插入点）必须归第 2 行行首，不能跨回上行末。
        var layout = Layout("a\nXY", maxWidth: double.PositiveInfinity, wrapping: TextWrapping.NoWrap);
        Assert.Equal(2, layout.Lines.Count);

        var snapshot = Snapshot(layout, preedit: "XY", preeditStart: 2, caretIndex: 2);

        Assert.True(snapshot.TryResolveCharPosition(0, out var x, out var y, out var h));
        Assert.Equal(layout.Lines[1].Bounds.Y + 20, y, 3);
        Assert.Equal(layout.Lines[1].Bounds.Height, h, 3);
        // 行首插入点 X = 该行 caret X + origin。
        Assert.Equal(10 + layout.GetCaretX(2, TextAffinity.LineStart), x, 3);
    }

    [Fact]
    public void PreeditCharAtSoftWrapSharedOffsetResolvesToLowerLine()
    {
        // 窄宽折行：预编辑 "abcd" 跨软换行；折行点之后的字符归属下行行首。
        var width = TextTestHelpers.BodyFont.Size * 1.2;
        var layout = Layout("abcd", maxWidth: width);
        Assert.Equal(2, layout.Lines.Count);
        var shared = layout.Lines[0].End;
        Assert.Equal(shared, layout.Lines[1].Start);
        Assert.False(layout.Text[shared - 1] is '\n' or '\r');

        var snapshot = Snapshot(layout, preedit: "abcd", preeditStart: 0, caretIndex: 0);

        // 折行点处的字符插入点（即下行首字符）必须在下行。
        Assert.True(snapshot.TryResolveCharPosition((uint)shared, out _, out var y, out _));
        Assert.Equal(layout.Lines[1].Bounds.Y + 20, y, 3);

        // 折行点之前的字符仍在上行。
        Assert.True(snapshot.TryResolveCharPosition((uint)(shared - 1), out _, out var upperY, out _));
        Assert.Equal(layout.Lines[0].Bounds.Y + 20, upperY, 3);
    }

    [Fact]
    public void PreeditEndOffsetResolvesToInsertionPoint()
    {
        var layout = Layout("a\nXY", maxWidth: double.PositiveInfinity, wrapping: TextWrapping.NoWrap);
        var snapshot = Snapshot(layout, preedit: "XY", preeditStart: 2, caretIndex: 4);

        // charPos == preedit.Length 应答末尾插入点。
        Assert.True(snapshot.TryResolveCharPosition(2, out var x, out var y, out _));
        Assert.Equal(layout.Lines[1].Bounds.Y + 20, y, 3);
        Assert.Equal(10 + layout.GetCaretX(4), x, 3);

        // 越界偏移拒绝。
        Assert.False(snapshot.TryResolveCharPosition(3, out _, out _, out _));
    }

    [Fact]
    public void NoPreeditCharPosZeroUsesCapturedCaretAffinity()
    {
        // 无预编辑：charPos=0 应答真实 caret；共享 offset 上 caret 的归属必须跟随快照 Affinity。
        var width = TextTestHelpers.BodyFont.Size * 1.2;
        var layout = Layout("abcd", maxWidth: width);
        var shared = layout.Lines[0].End;

        var upper = Snapshot(layout, preedit: "", preeditStart: 0, caretIndex: shared, affinity: TextAffinity.LineEnd);
        var lower = Snapshot(layout, preedit: "", preeditStart: 0, caretIndex: shared, affinity: TextAffinity.LineStart);

        Assert.True(upper.TryResolveCharPosition(0, out _, out var upperY, out _));
        Assert.True(lower.TryResolveCharPosition(0, out _, out var lowerY, out _));
        Assert.Equal(layout.Lines[0].Bounds.Y + 20, upperY, 3);
        Assert.Equal(layout.Lines[1].Bounds.Y + 20, lowerY, 3);

        // 其他偏移无定义。
        Assert.False(upper.TryResolveCharPosition(1, out _, out _, out _));
    }
}
