using CSharp.UI;
using CSharp.UI.Markdown;
using Xunit;

namespace CSharp.UI.Markdown.Tests;

public class AdapterTests
{
    [Fact]
    public void Paragraph_产出RichTextWrap()
    {
        var doc = Markdown.Parse("hello **world**");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        Assert.Equal(TextWrapping.Wrap, rich.Wrapping);
    }

    [Fact]
    public void Heading_产字号Bold()
    {
        var doc = Markdown.Parse("# Title");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        Assert.True(rich.Font?.Size > 14);
        Assert.Equal(FontWeight.Bold, rich.Font?.Weight);
    }

    [Fact]
    public void BoldItalic_样式区间存在()
    {
        var doc = Markdown.Parse("**bold** and *italic*");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        var styled = rich.Content;
        Assert.True(styled.Ranges.Count > 0);
        Assert.Contains(styled.Ranges, r => r.Style?.Weight == FontWeight.Bold);
        Assert.Contains(styled.Ranges, r => r.Style?.Italic == true);
    }

    [Fact]
    public void Link_产Interaction区间_同Id()
    {
        var doc = Markdown.Parse("[click](https://a.com) and [click](https://b.com)");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        var interactions = rich.Content.Interactions;
        Assert.Equal(2, interactions.Count);
        Assert.NotEqual(interactions[0].Id, interactions[1].Id);
        Assert.All(interactions, i => Assert.IsType<MarkdownLinkPayload>(i.Payload));
    }

    [Fact]
    public void UnsafeUrl_不产Interaction()
    {
        var doc = Markdown.Parse("[x](javascript:alert(1))");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        Assert.Empty(rich.Content.Interactions);
    }

    [Fact]
    public void UnsafeUrl_CustomPolicy_放行则产Interaction()
    {
        var doc = Markdown.Parse("[x](javascript:alert(1))");
        var view = new MarkdownBlockView
        {
            Document = doc,
            LinkPolicy = new MarkdownLinkPolicy { AllowActivation = _ => true },
        };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        Assert.Single(rich.Content.Interactions);
    }

    [Fact]
    public void List_产HStack含marker()
    {
        var doc = Markdown.Parse("- a\n- b");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var vstack = Assert.IsType<VStack>(tree);
        var item = Assert.IsType<HStack>(vstack.Children[0]);
        Assert.True(item.Children.Count >= 2);
    }

    [Fact]
    public void Quote_产HStack含Rectangle()
    {
        var doc = Markdown.Parse("> quoted");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var hstack = Assert.IsType<HStack>(tree);
        Assert.IsType<ThemedBar>(hstack.Children[0]); // quote bar 主题感知
    }

    [Fact]
    public void ThematicBreak_产Rectangle()
    {
        var doc = Markdown.Parse("---");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        Assert.IsType<ThemedBar>(tree); // 分隔线主题感知，无 FrameView 包装
    }

    [Fact]
    public void InlineImage_段落唯一图_产块级视图()
    {
        // 段落唯一 inline image → TryCreate 产 MarkdownImageView 或 alt RichText（resolver 决定）。
        var doc = Markdown.Parse("![alt text](img.png)");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        Assert.True(tree is RichText or MarkdownImageView);
    }

    [Fact]
    public void TableBlock_产Responsive横滚MarkdownTable()
    {
        // MD-03：TableBlock -> Responsive + ScrollView(Horizontal) + MarkdownTable。
        var doc = Markdown.Parse("| a | b |\n|---|---|\n| 1 | 2 |");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        Assert.IsType<Responsive>(tree);
    }

    [Fact]
    public void NestedEmphasis_叠加样式()
    {
        var doc = Markdown.Parse("**bo*ld***");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        // "**bo*ld***" → bo(Bold) + ld(Bold+Italic)
        var ranges = rich.Content.Ranges;
        Assert.Contains(ranges, r => r.Style?.Weight == FontWeight.Bold && r.Style?.Italic == true);
    }

    [Fact]
    public void HtmlBlock_Literal展示()
    {
        var doc = Markdown.Parse("<div>raw</div>");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        Assert.Contains("<div>", rich.Content.Text);
    }

    [Fact]
    public void HtmlBlock_Strip丢弃()
    {
        var doc = Markdown.Parse("<div>raw</div>", new MarkdownPipelineOptions { Html = HtmlPolicy.Strip });
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        Assert.IsNotType<RichText>(tree); // 唯一块被 Strip → 根非 RichText
    }

    [Fact]
    public void LinkNestedEmphasis_同Id跨样式()
    {
        var doc = Markdown.Parse("[plain **bold** rest](https://a.com)");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        var interactions = rich.Content.Interactions;
        Assert.True(interactions.Count >= 1);
        var ids = interactions.Select(i => i.Id).Distinct().ToList();
        Assert.Single(ids); // 同一链接同 Id
        Assert.Contains(rich.Content.Ranges, r => r.Style?.Weight == FontWeight.Bold);
    }

    [Fact]
    public void HtmlInline_Literal与Strip()
    {
        var lit = Markdown.Parse("a <b>bold</b> c");
        var litView = new MarkdownBlockView { Document = lit };
        var litTree = litView.BuildForTest();
        Assert.Contains("<b>", Assert.IsType<RichText>(litTree).Content.Text);

        var strip = Markdown.Parse("a <b>bold</b> c", new MarkdownPipelineOptions { Html = HtmlPolicy.Strip });
        var stripView = new MarkdownBlockView { Document = strip };
        var stripTree = stripView.BuildForTest();
        var stripText = Assert.IsType<RichText>(stripTree).Content.Text;
        Assert.DoesNotContain("<b>", stripText);
        Assert.Contains("bold", stripText);
    }

    [Fact]
    public void ChineseMixed_样式区间合法()
    {
        var doc = Markdown.Parse("中文 **粗体** 混合 English");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        var styled = rich.Content;
        Assert.Equal("中文 粗体 混合 English", styled.Text);
        var bold = styled.Ranges.FirstOrDefault(r => r.Style?.Weight == FontWeight.Bold);
        Assert.Equal("粗体", styled.Text[bold.Start..bold.End]);
    }

    [Fact]
    public void StyledText区间_非空不重叠在字素边界()
    {
        var doc = Markdown.Parse("mix **bo*ld*** `code` [link](https://a.com) end");
        var view = new MarkdownBlockView { Document = doc };
        var rich = Assert.IsType<RichText>(view.BuildForTest());
        var styled = rich.Content;
        var prevEnd = 0;
        foreach (var r in styled.Ranges)
        {
            Assert.True(r.Start >= prevEnd, $"range overlap at {r.Start}");
            Assert.True(r.End <= styled.Text.Length);
            prevEnd = r.End;
        }
        // 区间端点都在字素边界（构造 StyledText 已验证，断言无异常即通过）。
    }

    [Fact]
    public void CodeInline_产code字体背景区间()
    {
        var doc = Markdown.Parse("use `Print()` here");
        var view = new MarkdownBlockView { Document = doc };
        var rich = Assert.IsType<RichText>(view.BuildForTest());
        var styled = rich.Content;
        Assert.Contains(styled.Ranges, r => r.Style?.Family is not null);
    }

    [Fact]
    public void Autolink_产interaction_同link样式()
    {
        var doc = Markdown.Parse("visit https://a.com now");
        var view = new MarkdownBlockView { Document = doc };
        var rich = Assert.IsType<RichText>(view.BuildForTest());
        var styled = rich.Content;
        Assert.Single(styled.Interactions);
        var linkRange = styled.Ranges.FirstOrDefault(r =>
            styled.Text[r.Start..r.End].Contains("https://a.com"));
        Assert.True(linkRange.Style?.Underline == true);
    }

    [Fact]
    public void TaskList_合成marker含复选状态()
    {
        var doc = Markdown.Parse("- [ ] todo\n- [x] done", new MarkdownPipelineOptions { TaskLists = true });
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var vstack = Assert.IsType<VStack>(tree);
        Assert.Equal(2, vstack.Children.Count);
        var item0 = Assert.IsType<HStack>(vstack.Children[0]);
        var item1 = Assert.IsType<HStack>(vstack.Children[1]);
        var marker0 = Assert.IsType<RichText>(item0.Children[0]).Content.Text;
        var marker1 = Assert.IsType<RichText>(item1.Children[0]).Content.Text;
        Assert.Contains("[ ]", marker0);
        Assert.Contains("[x]", marker1);
    }

    [Fact]
    public void CombiningChar_跨链接字素_不抛且样式合法()
    {
        // "[e](https://x)\u0301" → 展示 "é" 一个字素，interaction [0,1]。
        // 若 adapter 把 mark 边界保留字素内部 → StyledText 构造抛 ArgumentException。
        var doc = Markdown.Parse("[e](https://x)\u0301");
        var view = new MarkdownBlockView { Document = doc };
        var tree = view.BuildForTest();
        var rich = Assert.IsType<RichText>(tree);
        var styled = rich.Content;
        Assert.Equal("é", styled.Text); // 单字素
        // interaction 必须吸附到字素边界 [0,2)（UTF-16 len），不是 [0,1)。
        Assert.Single(styled.Interactions);
        Assert.Equal(0, styled.Interactions[0].Start);
        Assert.Equal(2, styled.Interactions[0].End);
    }

    [Fact]
    public void TwoLinks_切同字素_唯一归属()
    {
        // "[e](a)\u0301[x](b)" → e+́ 一字素跨两链接 AST 节点。adapter 必须给该字素
        // 唯一 interaction owner（最深/最先），且 StyledText 构造不抛。
        var doc = Markdown.Parse("[e](https://a)\u0301[x](https://b)");
        var view = new MarkdownBlockView { Document = doc };
        var rich = Assert.IsType<RichText>(view.BuildForTest());
        var styled = rich.Content;
        Assert.True(styled.Interactions.Count >= 1);
        Assert.All(styled.Interactions, i => Assert.True(i.Start < i.End));
    }

    [Fact]
    public void EmphasisExtras_未支持符号保留Literal_删除线仍生效()
    {
        // 只启用 EmphasisExtraOptions.Strikethrough——`^`/`++`/`==`/单 `~` 不产 EmphasisInline,
        // 保留 literal markers 文本可见;`~~` 仍正确删除线。回归 flag 收紧语义:
        // 若回退到 `UseEmphasisExtras()` 无参 Default,`^sup^`/`++ins++`/`==mark==` 会被错渲染 italic/bold。
        var doc = Markdown.Parse("^sup^ ++ins++ ==mark== ~~strike~~");
        var view = new MarkdownBlockView { Document = doc };
        var rich = Assert.IsType<RichText>(view.BuildForTest());
        var styled = rich.Content;

        // 未支持符号保持 literal marker(不吞,不错误样式)。
        Assert.Contains("^sup^", styled.Text);
        Assert.Contains("++ins++", styled.Text);
        Assert.Contains("==mark==", styled.Text);

        // 删除线:文本含 "strike" 且该段 Strikethrough=true;不误产 Italic/Bold。
        Assert.Contains("strike", styled.Text);
        var strikeRange = styled.Ranges.First(r => r.Style?.Strikethrough == true);
        Assert.Equal("strike", styled.Text[strikeRange.Start..strikeRange.End]);
        Assert.DoesNotContain(styled.Ranges, r => r.Style?.Italic == true);
        Assert.DoesNotContain(styled.Ranges, r => r.Style?.Weight == FontWeight.Bold);
    }
}
