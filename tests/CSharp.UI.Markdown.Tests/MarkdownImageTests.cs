using CSharp.UI;
using CSharp.UI.Markdown;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Markdown.Tests;

/// <summary>
/// MD-04 块级唯一图片的永久回归：runtime 真实 TryLoad 驱动、源/失败切换、几何契约。
/// 仅覆盖行为可观察结果（帧/文本/调用计数），不涉内部字段或 record equality。
/// </summary>
public class MarkdownImageTests
{
    private static UiRuntime NewRuntime(View view, StubImageService images)
        => new(view, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService(),
            ImageService = images,
        });

    [Fact]
    public void 单图段_小图保持Natural不放大()
    {
        var doc = Markdown.Parse("![tiny](t.png)");
        var svc = new StubImageService();
        svc.Register("t.png", 40, 20);
        var view = new MarkdownBlockView
        {
            Document = doc,
            ImagePolicy = new MarkdownImagePolicy { Resolve = u => u },
        };

        using var rt = NewRuntime(view, svc);
        rt.SetViewport(new Size(400, 300), 1.0);
        rt.ProcessPendingWork();
        rt.BuildFrame();

        var img = rt.Root.FindDescendants<Image>().Single();
        Assert.InRange(img.Frame.Width, 39.5, 40.5);
        Assert.InRange(img.Frame.Height, 19.5, 20.5);
    }

    [Fact]
    public void 单图段_超宽按宽等比缩()
    {
        var doc = Markdown.Parse("![w](w.png)");
        var svc = new StubImageService();
        svc.Register("w.png", 800, 200);
        var view = new MarkdownBlockView
        {
            Document = doc,
            ImagePolicy = new MarkdownImagePolicy { Resolve = u => u },
        };

        using var rt = NewRuntime(view, svc);
        rt.SetViewport(new Size(400, 300), 1.0);
        rt.ProcessPendingWork();
        rt.BuildFrame();

        var img = rt.Root.FindDescendants<Image>().Single();
        Assert.InRange(img.Frame.Width, 399, 401);
        Assert.InRange(img.Frame.Height, 99, 101); // 800x200 → 400x100
    }

    [Fact]
    public void 单图段_本地源TryLoad失败显示Alt_Image空帧()
    {
        var doc = Markdown.Parse("![miss-text](miss.png)");
        var svc = new StubImageService();
        var view = new MarkdownBlockView
        {
            Document = doc,
            ImagePolicy = new MarkdownImagePolicy { Resolve = u => u },
        };

        using var rt = NewRuntime(view, svc);
        rt.SetViewport(new Size(400, 300), 1.0);
        rt.ProcessPendingWork();
        rt.BuildFrame();

        var img = rt.Root.FindDescendants<Image>().Single();
        var alt = rt.Root.FindDescendants<RichText>().Single();
        Assert.True(img.Frame.IsEmpty);
        Assert.True(alt.Frame.Height > 0);
        Assert.Equal("miss-text", ((RichText)alt.View).Content.Text);
        Assert.True(svc.FailureCount >= 1);
    }

    [Fact]
    public void 单图段_远端默认拒绝_Resolver零调用显示Alt()
    {
        var calls = 0;
        var doc = Markdown.Parse("![alt](https://example.com/a.png)");
        var view = new MarkdownBlockView
        {
            Document = doc,
            ImagePolicy = new MarkdownImagePolicy { Resolve = _ => { calls++; return "x"; } },
        };

        using var rt = NewRuntime(view, new StubImageService());
        rt.SetViewport(new Size(400, 300), 1.0);
        rt.ProcessPendingWork();
        rt.BuildFrame();

        Assert.Equal(0, calls);
        var rich = rt.Root.FindDescendants<RichText>().Single();
        Assert.Equal("alt", ((RichText)rich.View).Content.Text);
        Assert.True(rich.Frame.Height > 0);
    }

    [Fact]
    public void 单图段_State切Source_新Aspect生效_旧缓存不残留()
    {
        var doc = Markdown.Parse("![i](x)");
        var svc = new StubImageService();
        svc.Register("a.png", 100, 40);
        svc.Register("b.png", 200, 160);
        var host = new SwitchHost(doc, "a.png");

        using var rt = NewRuntime(host, svc);
        rt.SetViewport(new Size(400, 300), 1.0);
        rt.ProcessPendingWork();
        rt.BuildFrame();
        var a = rt.Root.FindDescendants<Image>().Single();
        Assert.InRange(a.Frame.Width, 99, 101);
        Assert.InRange(a.Frame.Height, 39, 41);

        host.Source!.Value = "b.png";
        rt.ProcessPendingWork();
        rt.BuildFrame();
        var b = rt.Root.FindDescendants<Image>().Single();
        Assert.InRange(b.Frame.Width, 199, 201);
        Assert.InRange(b.Frame.Height, 159, 161);
    }

    [Fact]
    public void 单图段_Fail后换可用Source_重试成功()
    {
        var doc = Markdown.Parse("![alt](x)");
        var svc = new StubImageService();
        svc.Register("later.png", 60, 24);
        var host = new SwitchHost(doc, "missing.png");

        using var rt = NewRuntime(host, svc);
        rt.SetViewport(new Size(400, 300), 1.0);
        rt.ProcessPendingWork();
        rt.BuildFrame();
        var alt = rt.Root.FindDescendants<RichText>().Single();
        Assert.True(alt.Frame.Height > 0);

        host.Source!.Value = "later.png";
        rt.ProcessPendingWork();
        rt.BuildFrame();
        var img = rt.Root.FindDescendants<Image>().Single();
        Assert.InRange(img.Frame.Width, 59, 61);
        Assert.InRange(img.Frame.Height, 23, 25);
    }

    /// <summary>把 Resolve 的 url→source 放进 State，驱动同一节点上的 wrapper OnUpdate。</summary>
    private sealed class SwitchHost : Component
    {
        private readonly MarkdownDocument _doc;
        private readonly string _initial;

        public SwitchHost(MarkdownDocument doc, string initialSource)
        {
            _doc = doc;
            _initial = initialSource;
        }

        public State<string>? Source { get; private set; }

        protected override View Build()
        {
            Source = UseState("source", _initial);
            var src = Source.Value;
            return new MarkdownBlockView
            {
                Document = _doc,
                ImagePolicy = new MarkdownImagePolicy { Resolve = _ => src },
            };
        }
    }
}
