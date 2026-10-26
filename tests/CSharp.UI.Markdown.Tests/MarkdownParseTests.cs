using System.Collections.Concurrent;
using System.Threading.Tasks;
using CSharp.UI.Markdown;
using Xunit;

namespace CSharp.UI.Markdown.Tests;

public class MarkdownParseTests
{
    [Fact]
    public void Parse_null源文本_抛ArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => Markdown.Parse(null!));
    }

    [Fact]
    public void Parse_中文全要素_产出可遍历AST()
    {
        var doc = Markdown.Parse(
            "# 标题一\n\n正文 **粗体** *斜体* `code` [链接](https://example.com)。\n\n" +
            "- 项一\n- 项二\n\n" +
            "> 引用\n\n" +
            "```csharp\nvar x = 1;\n```\n\n" +
            "| a | b |\n|---|---|\n| 1 | 2 |\n\n" +
            "~~strike~~\n");

        Assert.NotNull(doc);
        Assert.False(string.IsNullOrEmpty(doc.Source));
        // AST 顶层至少应包含标题、段落、列表、引用、围栏代码、表格——内部断言块类型。
        var kinds = new List<string>();
        foreach (var block in doc.Ast)
        {
            kinds.Add(block.GetType().Name);
        }

        Assert.Contains("HeadingBlock", kinds);
        Assert.Contains("ParagraphBlock", kinds);
        Assert.Contains("ListBlock", kinds);
        Assert.Contains("QuoteBlock", kinds);
        Assert.Contains("FencedCodeBlock", kinds);
        Assert.Contains("Table", kinds);
    }

    [Fact]
    public void Parse_同source同options_返回同实例()
    {
        var a = Markdown.Parse("# hi");
        var b = Markdown.Parse("# hi");
        Assert.Same(a, b);
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Parse_同source异options_重Parse新实例()
    {
        var a = Markdown.Parse("# hi", new MarkdownPipelineOptions { PipeTables = true });
        var b = Markdown.Parse("# hi", new MarkdownPipelineOptions { PipeTables = false });
        Assert.NotSame(a, b);
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Parse_Options各字段影响AST()
    {
        // PipeTables 关闭 → `|a|b|` 应解析为普通段落而非 Table。
        var on = Markdown.Parse("| a | b |\n|---|---|\n| 1 | 2 |", new MarkdownPipelineOptions { PipeTables = true });
        var off = Markdown.Parse("| a | b |\n|---|---|\n| 1 | 2 |", new MarkdownPipelineOptions { PipeTables = false });
        var onKinds = on.Ast.Select(b => b.GetType().Name).ToList();
        var offKinds = off.Ast.Select(b => b.GetType().Name).ToList();
        Assert.Contains("Table", onKinds);
        Assert.DoesNotContain("Table", offKinds);

        // EmphasisExtras 关闭 → ~~strike~~ 不解析为 strikethrough。
        var eeOn = Markdown.Parse("~~x~~", new MarkdownPipelineOptions { EmphasisExtras = true });
        var eeOff = Markdown.Parse("~~x~~", new MarkdownPipelineOptions { EmphasisExtras = false });
        var eeOnInlines = ((Markdig.Syntax.ParagraphBlock)eeOn.Ast[0]).Inline!.Select(i => i.GetType().Name).ToList();
        var eeOffInlines = ((Markdig.Syntax.ParagraphBlock)eeOff.Ast[0]).Inline!.Select(i => i.GetType().Name).ToList();
        Assert.Contains("EmphasisInline", eeOnInlines);
        Assert.DoesNotContain("EmphasisInline", eeOffInlines);

        // AutoLinks 关闭 → 裸 URL 不产链接节点；开 → 产 LinkInline（Markdig 1.5.0 归一化为 LinkInline）。
        var alOn = Markdown.Parse("https://x.com", new MarkdownPipelineOptions { AutoLinks = true });
        var alOff = Markdown.Parse("https://x.com", new MarkdownPipelineOptions { AutoLinks = false });
        var alOnInlines = ((Markdig.Syntax.ParagraphBlock)alOn.Ast[0]).Inline!.Select(i => i.GetType().Name).ToList();
        var alOffInlines = ((Markdig.Syntax.ParagraphBlock)alOff.Ast[0]).Inline!.Select(i => i.GetType().Name).ToList();
        Assert.Contains("LinkInline", alOnInlines);
        Assert.DoesNotContain("LinkInline", alOffInlines);
        var tlOn = Markdown.Parse("- [ ] todo", new MarkdownPipelineOptions { TaskLists = true });
        var tlOff = Markdown.Parse("- [ ] todo", new MarkdownPipelineOptions { TaskLists = false });
        var tlOnList = (Markdig.Syntax.ListBlock)tlOn.Ast[0];
        var tlOffList = (Markdig.Syntax.ListBlock)tlOff.Ast[0];
        var tlOnItem = (Markdig.Syntax.ListItemBlock)tlOnList[0];
        var tlOffItem = (Markdig.Syntax.ListItemBlock)tlOffList[0];
        var tlOnPara = (Markdig.Syntax.ParagraphBlock)tlOnItem[0];
        var tlOffPara = (Markdig.Syntax.ParagraphBlock)tlOffItem[0];
        // TaskList 项由 Markdig 解析为 TaskList inline 节点。
        Assert.Contains(tlOnPara.Inline!, i => i.GetType().Name == "TaskList");
        Assert.DoesNotContain(tlOffPara.Inline!, i => i.GetType().Name == "TaskList");
    }

    [Fact]
    public void Parse_HtmlPolicy两值进键()
    {
        var literal = Markdown.Parse("<div>x</div>", new MarkdownPipelineOptions { Html = HtmlPolicy.Literal });
        var strip = Markdown.Parse("<div>x</div>", new MarkdownPipelineOptions { Html = HtmlPolicy.Strip });
        Assert.NotSame(literal, strip);
        Assert.False(literal.Equals(strip));
    }

    [Fact]
    public async Task Parse_并发同键异键_值正确()
    {
        // 公开静态缓存并发安全:N 线程同时 Parse 同 source + 不同 source,
        // 各自拿到 Source/Options 值正确的实例,无 LruCache race 异常。
        const int threadCount = 16;
        const int iterations = 32;
        var shared = "# shared\n\nbody";
        var uniquePrefix = "# unique ";
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    // 同 key(共享 source)+ 异 key(每线程独立 source)。
                    var expectedUnique = uniquePrefix + t + "-" + i;
                    var d1 = Markdown.Parse(shared);
                    var d2 = Markdown.Parse(expectedUnique);

                    // 关键不变量:Source/Options 值正确。并发下同 key 可能返回
                    // 同实例或 evict 后重 parse 的新实例——断言只看值。
                    Assert.Equal(shared, d1.Source);
                    Assert.Equal(new MarkdownPipelineOptions(), d1.Options);
                    Assert.Equal(expectedUnique, d2.Source);
                    Assert.Equal(new MarkdownPipelineOptions(), d2.Options);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Empty(errors);
    }
}
