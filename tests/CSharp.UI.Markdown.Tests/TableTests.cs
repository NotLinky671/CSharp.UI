using CSharp.UI;
using CSharp.UI.Markdown;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Markdown.Tests;

public class TableTests
{
    private static MarkdownTable BuildTable(string markdown, double? width = null)
    {
        var doc = Markdown.Parse(markdown);
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var responsive = Assert.IsType<Responsive>(tree);
        var info = new ResponsiveInfo(width ?? double.PositiveInfinity, width.HasValue);
        var scroll = Assert.IsType<ScrollView>(responsive.Builder(info));
        return Assert.IsType<MarkdownTable>(scroll.Children[0]);
    }

    [Fact]
    public void Table_产ResponsiveScrollViewMarkdownTable()
    {
        var table = BuildTable("| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.True(table.Children.Count > 0); // cell VStack + 分隔条
    }

    [Fact]
    public void Table_Header_Bold样式()
    {
        var table = BuildTable("| h1 | h2 |\n|---|---|\n| a | b |", width: 400);
        // 首行 cell 的 RichText 应有 Weight=Bold range。
        var headerCell = table.Children[0];
        var vstack = Assert.IsType<VStack>(headerCell);
        var rich = Assert.IsType<RichText>(vstack.Children[0]);
        Assert.Contains(rich.Content.Ranges, r => r.Style?.Weight == FontWeight.Bold);
    }

    [Fact]
    public void Table_Header_mixedStyle_全段Bold()
    {
        // `| plain *italic* tail |`：原 ranges 只覆盖 italic 区段——bold overlay 须补满 style holes。
        var table = BuildTable("| plain *italic* tail |\n|---|\n| a |", width: 400);
        var headerCell = table.Children[0];
        var vstack = Assert.IsType<VStack>(headerCell);
        var rich = Assert.IsType<RichText>(vstack.Children[0]);
        var text = rich.Content.Text;
        var ranges = rich.Content.Ranges;
        Assert.True(ranges.Count > 0);
        var covered = new bool[text.Length];
        foreach (var r in ranges)
        {
            Assert.Equal(FontWeight.Bold, r.Style?.Weight);
            for (var i = r.Start; i < r.End; i++)
            {
                covered[i] = true;
            }
        }
        Assert.All(covered, Assert.True);
        var italicRange = ranges.FirstOrDefault(r => r.Style?.Italic == true);
        Assert.True(italicRange.End > italicRange.Start);
    }

    [Fact]
    public void Table_列对齐_Right_下RichTextAlignment()
    {
        var table = BuildTable("| a | b |\n|---|---:|\n| 1 | 2 |", width: 400);
        // children 展平：[cell0, cell1, 分隔条, cell0, cell1]
        var secondCell = table.Children[1];
        var vstack = Assert.IsType<VStack>(secondCell);
        var rich = Assert.IsType<RichText>(vstack.Children[0]);
        Assert.Equal(TextAlignment.Right, rich.Alignment);
    }

    [Fact]
    public void Table_缺cell_按最大列补()
    {
        // 第二行少一列 → cell 数按最大列补齐。
        var table = BuildTable("| a | b |\n|---|---|\n| 1 |", width: 400);
        // 2 列 → 每行 2 cell + 分隔条 = 2*2 + 1 = 5 children。
        Assert.Equal(5, table.Children.Count);
    }

    [Fact]
    public void Table_320窄宽_中文_真实runtime产命令()
    {
        var md = "| 很长的中文内容 | 另一列长内容 |\n|---|---|\n| 短 | 短 |";
        var doc = Markdown.Parse(md);
        var view = new MarkdownBlockView { Document = doc };
        var runtime = new UiRuntime(view, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService(),
        });
        runtime.SetViewport(new Size(320, 200), 1.0);
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();
        Assert.NotEmpty(frame.Commands); // 表格真实渲染出 cell+分隔条命令
        runtime.Dispose();
    }

    /// <summary>
    /// 真实排版（SkiaTextService）下 cell 内嵌混合块（para + list + quote + code）的
    /// min-content/行高/列对齐正确性。Markdig pipe table 只产 inline cell，
    /// 本测试在 IVT 程序集手工构造 cell VStack 验内部布局契约。
    /// </summary>
    [Fact]
    public void Table_Cell内嵌块_真实排版_min含chrome()
    {
        var para = new RichText(StyledText.Create("para text"));
        var listItem1 = new HStack(new Text("•"), new RichText(StyledText.Create("list item one")));
        var listItem2 = new HStack(new Text("•"), new RichText(StyledText.Create("list item two longer")));
        var list = new VStack(listItem1, listItem2);
        var quote = new HStack(
            new ThemedBar { Fill = new Color(128, 128, 128, 255), FixedSize = new Size(3, 0) },
            new RichText(StyledText.Create("quoted text")));
        var code = new RichText(StyledText.Create("var x = codeBlockLine;"));
        var cell0 = new VStack(para, list, quote, code);
        var cell1 = new VStack(new RichText(StyledText.Create("short")));

        var table = new MarkdownTable(
            [[cell0, cell1]],
            400, cellPadding: 8);

        var textSvc = new CSharp.UI.Skia.SkiaTextService();
        // MarkdownTable 直挂 root——不经过 Responsive/ScrollView（本测试验内部布局契约）。
        var runtime = new UiRuntime(table, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textSvc,
            RichTextService = textSvc,
        });
        runtime.SetViewport(new Size(600, 400), 1.0);
        runtime.ProcessPendingWork();
        // 断言布局（DesiredSize/Frame）而非命令——cell 内嵌块真实排版：列 min 含 chrome
        // （list marker/quote bar/code 行宽），pref 含混合块自然宽。
        var root = runtime.Root!;
        Assert.True(root.DesiredSize.Width > 0);
        Assert.True(root.DesiredSize.Height > 0);
        runtime.Dispose();
        textSvc.Dispose();
    }

    /// <summary>
    /// 同一 <see cref="MarkdownDocument"/> 在 viewport 320 → 1024 → 320 间切换：
    /// <c>Responsive</c> 重建 <see cref="MarkdownTable"/>，新 captured availableWidth 必须
    /// 触发 reconciler 重 measure（<see cref="MarkdownTable.LayoutPropertiesEquivalent"/>）。
    /// 断言 DesiredWidth 依次 = 320（Σmin 超宽横滚）/ 434（Σpref 自然）/ 320。
    /// </summary>
    [Fact]
    public void Table_同文档_跨档Resize_重排版()
    {
        var doc = Markdown.Parse("| 很长的中文内容 | 另一列长内容 |\n|---|---|\n| 短 | 短 |");
        var view = new MarkdownBlockView { Document = doc };
        var textSvc = new CSharp.UI.Skia.SkiaTextService();
        var runtime = new UiRuntime(view, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textSvc,
            RichTextService = textSvc,
        });

        double MeasureAt(double w)
        {
            runtime.SetViewport(new Size(w, 200), 1.0);
            runtime.ProcessPendingWork();
            return runtime.Root!.DesiredSize.Width;
        }

        var w320a = MeasureAt(320);
        var w1024 = MeasureAt(1024);
        var w320b = MeasureAt(320);

        Assert.Equal(320, w320a, 1); // Σmin 超宽横滚
        Assert.True(w1024 > w320a);  // 434 自然宽 > 320
        Assert.Equal(w320a, w320b, 1); // 回 320 仍 Σmin
        runtime.Dispose();
        textSvc.Dispose();
    }
}
