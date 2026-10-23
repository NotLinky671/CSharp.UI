using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TE-01 核心契约：TextLayoutResult 值模型/查询边界、selection 查询语义、
/// EditOperation transaction 边界与 SanitizeMultiLine 归一化。
/// 真实排版行为（尾随空段/软换行双坐标/行尾空白/CRLF）由
/// tests/CSharp.UI.Text.Tests 用 SkiaTextService 验证。
/// </summary>
public sealed class TextEditorEditingLayoutTests
{
    private static TextLayoutResult BuildLayout(
        string text,
        IReadOnlyList<TextLineInfo>? lines = null,
        IReadOnlyList<IReadOnlyList<CaretPosition>>? caretsByLine = null)
    {
        var stops = new List<int>();
        var xs = new List<double>();
        var clusters = new List<int>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            clusters.Add(enumerator.ElementIndex);
        }

        if (clusters.Count == 0 || clusters[0] != 0)
        {
            clusters.Insert(0, 0);
        }

        if (clusters[^1] != text.Length)
        {
            clusters.Add(text.Length);
        }

        foreach (var s in clusters)
        {
            stops.Add(s);
            xs.Add(s); // 简化：X=offset，仅供值模型/查询语义验证
        }

        return new TextLayoutResult(
            text,
            FontDescription.Default,
            1.0,
            new Size(text.Length, 10),
            lines ?? [new TextLineInfo(0, text.Length, new Rect(0, 0, text.Length, 10), 8)],
            stops,
            xs,
            caretsByLine: caretsByLine);
    }

    [Fact]
    public void Constructor_RejectsIncompleteCaretMapping()
    {
        var line = new TextLineInfo(0, 3, new Rect(0, 0, 10, 10), 8);

        Assert.Throws<ArgumentException>(() => new TextLayoutResult(
            "abc", FontDescription.Default, 1.0, new Size(10, 10), [line], [0, 3], [0]));

        Assert.Throws<ArgumentException>(() => new TextLayoutResult(
            "abc", FontDescription.Default, 1.0, new Size(10, 10), [line], [0], [0]));
    }

    [Fact]
    public void CaretsByLine_IsLazyAndStableForDisplayLayout()
    {
        var layout = BuildLayout("ab\ncd", lines:
        [
            new TextLineInfo(0, 2, new Rect(0, 0, 20, 10), 8),
            new TextLineInfo(3, 5, new Rect(0, 10, 20, 10), 18),
        ]);

        // 首次访问构造默认表；同一实例返回同一表（缓存，不每次重建）。
        var first = layout.CaretsByLine;
        var second = layout.CaretsByLine;
        Assert.Same(first, second);
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public void GetCaretBounds_UsesAffinityResolvedLineNotNextLineFallback()
    {
        // 硬换行后 offset：请求 LineEnd 归属时不得把行错误回退到下一行。
        var layout = BuildLayout("ab\ncd", lines:
        [
            new TextLineInfo(0, 2, new Rect(0, 0, 20, 10), 8),
            new TextLineInfo(3, 5, new Rect(0, 10, 20, 10), 18),
        ]);

        // offset 3 是下一行 Start（\n 后）：LineStart 归属应取下一行（Y=10）。
        var bounds = layout.GetCaretBounds(3, TextAffinity.LineStart);
        Assert.Equal(10, bounds.Y, 3);

        // LineEnd 归属 offset 3 在下行 Start，非共享软换行 → 仍归下行（非歧义忽略 affinity）。
        var boundsLineEnd = layout.GetCaretBounds(3, TextAffinity.LineEnd);
        Assert.Equal(10, boundsLineEnd.Y, 3); // 不落到上一行
    }

    [Fact]
    public void GetRangeRects_UsesLineLocalClipX()
    {
        // 跨行选区：end 落在下一行时，第一行右缘用本行行末 X，不读下一行 start X。
        var layout = BuildLayout("ab\ncd", lines:
        [
            new TextLineInfo(0, 2, new Rect(0, 0, 20, 10), 8),   // 行0 X∈[0,20]
            new TextLineInfo(3, 5, new Rect(0, 10, 20, 10), 18), // 行1 X∈[0,20]
        ]);

        var rects = layout.GetRangeRects(0, 5);
        // 行0 rect 右缘 clip 在 LineEndIndex(0)=2 的局部 X（不读 end=5 或下行 start=3）。
        Assert.Equal(layout.GetCaretX(2), rects[0].Right, 3);
        Assert.Equal(0, rects[0].X, 3);
        Assert.Equal(0, rects[0].Y, 3);
    }

    [Fact]
    public void GetRangeRects_EmptyRangeProducesNoRect()
    {
        var layout = BuildLayout("ab\ncd", lines:
        [
            new TextLineInfo(0, 2, new Rect(0, 0, 20, 10), 8),
            new TextLineInfo(3, 5, new Rect(0, 10, 20, 10), 18),
        ]);

        var rects = layout.GetRangeRects(2, 2);
        Assert.Empty(rects); // 空选区不产 marker
    }

    [Fact]
    public void GetRangeRects_EndAtNextLineStartDoesNotMisselect()
    {
        var layout = BuildLayout("ab\ncd", lines:
        [
            new TextLineInfo(0, 3, new Rect(0, 0, 20, 10), 8),   // 行0 含 \\n@2（Skia 行 End 含 terminator）
            new TextLineInfo(3, 5, new Rect(0, 10, 20, 10), 18),
        ]);

        // 选 [0,3)：覆盖行0 'ab'+'\\n'(offset 2)、不含下一行 'cd'。
        // 产行0内容 rect + 行尾 newline marker（共2项），不产第二行内容 rect。
        var rects = layout.GetRangeRects(0, 3);
        Assert.Equal(2, rects.Count);
        Assert.Equal(0, rects[0].Y, 3);
        Assert.Equal(0, rects[1].Y, 3); // newline marker 与内容同行
        Assert.Equal(0, rects[1].Width, 3); // 零宽 marker
        Assert.DoesNotContain(rects, r => r.Y == 10 && r.Width > 0);
    }

    [Fact]
    public void GetRangeRects_StubEditingLayout_NewlineSelectionProducesMarker()
    {
        // 经真实 StubTextService 编辑排版（行 End 含 \n terminator、ContentEnd 不含）：
        // 选区覆盖换行符时行0产内容 rect + 零宽 newline marker，下行不误选。
        var service = new StubTextService();
        var layout = service.Layout(new TextLayoutRequest("ab\ncd")
        {
            MaxWidth = double.PositiveInfinity,
            Wrapping = TextWrapping.NoWrap,
            Editing = true,
        });

        var rects = layout.GetRangeRects(0, 3); // 'a','b','\n'
        Assert.Equal(2, rects.Count);
        Assert.Equal(0, rects[0].Y, 3);
        Assert.True(rects[0].Width > 0);                    // 'ab' 内容区
        Assert.Equal(rects[0].Y, rects[1].Y);               // marker 与内容同行
        Assert.Equal(0, rects[1].Width, 3);                 // 零宽 newline marker
        Assert.DoesNotContain(rects, r => r.Y > 0);

        // CRLF：行0 End 含整个 \r\n（2 字符），选区覆盖它同样产 marker，宽度不计 terminator。
        var layoutCrlf = service.Layout(new TextLayoutRequest("ab\r\ncd")
        {
            MaxWidth = double.PositiveInfinity,
            Wrapping = TextWrapping.NoWrap,
            Editing = true,
        });
        Assert.Equal(2, layoutCrlf.Lines.Count);
        Assert.Equal(4, layoutCrlf.Lines[0].End);   // [0,4) 含 'a','b','\r','\n'
        Assert.Equal(4, layoutCrlf.Lines[1].Start); // 下行 Start 与上行 End 对接
        var crlfRects = layoutCrlf.GetRangeRects(0, 4);
        Assert.Equal(2, crlfRects.Count);
        Assert.Equal(0, crlfRects[1].Width, 3);     // 零宽 marker（选中 terminator）
    }

    [Fact]
    public void SanitizeMultiLine_NormalizesLineEndings()
    {
        Assert.Equal("a\nb\ncd", TextInputEvent.SanitizeMultiLine("a\r\nb\rc\td"));
        Assert.Equal("a\nb", TextInputEvent.SanitizeMultiLine("a\nb"));
        Assert.Equal("\n\n", TextInputEvent.SanitizeMultiLine("\r\n\r\t")); // \r\n→\n、孤\r→\n、\t删
        Assert.Equal("abc", TextInputEvent.SanitizeMultiLine("abc"));
    }

    [Fact]
    public void EditOperation_TryCreate_NormalizeLineEndingsKeepsOriginalRange()
    {
        var range = new TextRange(1, 3); // 真实文档 offset
        Assert.True(EditOperation.TryCreate(
            range,
            "x\r\ny",
            TextEditKind.Paste,
            TextEditReason.Clipboard,
            scrubSingleLine: false,
            out var operation,
            out _,
            normalizeLineEndings: true));

        Assert.Equal(range, operation.Range); // Range 不被归一化长度回裁
        Assert.Equal("x\ny", operation.Replacement);
        Assert.False(operation.IsDeletion);
    }

    [Fact]
    public void EditOperation_TryCreate_RejectsInsertionThatNormalizesToEmpty()
    {
        // 全过滤插入（纯 \t）归一化后为空 → 拒绝，不当作删除执行。
        Assert.False(EditOperation.TryCreate(
            new TextRange(0, 0),
            "\t",
            TextEditKind.Paste,
            TextEditReason.Clipboard,
            scrubSingleLine: false,
            out var operation,
            out _,
            normalizeLineEndings: true));
        Assert.Equal(0, operation.Range.Start); // operation 无效（默认值）
    }

    [Fact]
    public void Editing_RequestFlagDistinguishesCacheKeys()
    {
        // 同配置 Editing true/false 的 record 相等性不同 → 不串缓存。
        var request = new TextLayoutRequest("a\nb")
        {
            MaxWidth = 100,
            Wrapping = TextWrapping.Wrap,
        };

        var editing = request with { Editing = true };
        var display = request with { Editing = false };

        Assert.NotEqual(editing, display);
        Assert.NotEqual(editing.GetHashCode(), display.GetHashCode());
    }
}
